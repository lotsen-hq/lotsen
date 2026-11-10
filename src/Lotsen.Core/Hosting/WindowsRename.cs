using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Lotsen.Hosting;

/// <summary>
/// Renames a file over another on Windows with POSIX semantics, which replaces the target even while readers hold it open, provided they share delete.
/// </summary>
/// <remarks>
/// A plain replace, as <see cref="File.Move(string, string, bool)"/> makes it, fails with access denied while any reader holds the target open, so a write of a tour that a request reads fails.
/// <c>UserOnlyStorageTests</c> pins the replace under a reader. The flags are documented on <c>FILE_RENAME_INFORMATION</c>:
/// https://learn.microsoft.com/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class WindowsRename
{
   private const uint Delete = 0x00010000;
   private const uint Synchronize = 0x00100000;
   private const uint OpenExisting = 3;
   private const int FileRenameInfoEx = 22;
   private const uint ReplaceIfExists = 0x1;
   private const uint PosixSemantics = 0x2;
   private const int ErrorInvalidFunction = 1;
   private const int ErrorNotSupported = 50;
   private const int ErrorInvalidParameter = 87;

   /// <summary>Renames the source file over the target.</summary>
   /// <returns>Whether it renamed the file, or false when the file system does not support the rename. Nothing was renamed then.</returns>
   /// <exception cref="IOException">The rename failed for another reason.</exception>
   /// <exception cref="UnauthorizedAccessException">Access to either file was denied.</exception>
   public static bool TryReplace(string source, string target)
   {
      using var handle = CreateFileW(source, Delete | Synchronize, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
      if (handle.IsInvalid)
      {
         throw ExceptionFor(Marshal.GetLastPInvokeError());
      }

      var info = RenameInfo(Path.GetFullPath(target));
      if (SetFileInformationByHandle(handle, FileRenameInfoEx, info, (uint)info.Length))
      {
         return true;
      }

      var error = Marshal.GetLastPInvokeError();
      return error is ErrorInvalidFunction or ErrorNotSupported or ErrorInvalidParameter ? false : throw ExceptionFor(error);
   }

   // FILE_RENAME_INFO: the flags, then the root directory handle at pointer alignment, the name's length in bytes, and the name with its terminating NUL.
   private static byte[] RenameInfo(string target)
   {
      var lengthOffset = IntPtr.Size * 2;
      var nameOffset = lengthOffset + sizeof(uint);
      var info = new byte[nameOffset + ((target.Length + 1) * sizeof(char))];
      BinaryPrimitives.WriteUInt32LittleEndian(info, ReplaceIfExists | PosixSemantics);
      BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(lengthOffset), (uint)(target.Length * sizeof(char)));
      MemoryMarshal.AsBytes(target.AsSpan()).CopyTo(info.AsSpan(nameOffset));
      return info;
   }

   private static Exception ExceptionFor(int error) => Marshal.GetExceptionForHR(unchecked((int)0x80070000 | error)) ?? new IOException($"The file could not be renamed (Windows error {error}).");

   [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
   private static partial SafeFileHandle CreateFileW(string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

   [LibraryImport("kernel32.dll", SetLastError = true)]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static partial bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, [In] byte[] info, uint size);
}
