using System.Diagnostics.CodeAnalysis;

namespace Lotsen.Git;

/// <summary>The rule for a file whose lines lotsen reads: a text file of at most <see cref="MaxSize"/> bytes at its commit.</summary>
internal static class ReadableFile
{
   /// <summary>The largest file in bytes whose lines lotsen reads.</summary>
   public const long MaxSize = 16 * 1024 * 1024;

   /// <summary>The size in bytes above which a file is large: lotsen withholds its text unless a caller asks for large files, and <see cref="GitRepository.FindFileAsync"/> reads none of its content in advance.</summary>
   public const long LargeSize = 1024 * 1024;

   /// <summary>Returns whether the blob is a readable file: not binary, and at most <see cref="MaxSize"/> bytes.</summary>
   /// <param name="file">The blob, or null when the path names no file at the commit, which is not readable.</param>
   public static bool Is([NotNullWhen(true)] FileBlob? file) => file is { Binary: false, Size: <= MaxSize };
}
