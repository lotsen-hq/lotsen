using System.Collections.Frozen;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.StaticFiles;

namespace Lotsen.Server;

/// <summary>
/// Serves the UI files embedded in the executable, without the token.
/// </summary>
/// <remarks>
/// The project embeds every file under its UI directory as a resource named <c>ui/</c> plus the file's relative path. An HTML file gets the response's style nonce in place of <see
/// cref="ContentSecurityPolicy.NoncePlaceholder"/>.
/// </remarks>
internal sealed class UiAssets
{
   private const string ResourcePrefix = "ui/";
   private const string IndexPath = "index.html";

   private readonly FrozenDictionary<string, UiAsset> _assets;

   private UiAssets(FrozenDictionary<string, UiAsset> assets) => _assets = assets;

   /// <summary>Loads the UI files from the resources of this assembly.</summary>
   public static UiAssets LoadEmbedded()
   {
      var assembly = typeof(UiAssets).Assembly;
      var contentTypes = new FileExtensionContentTypeProvider();
      var assets = new Dictionary<string, UiAsset>(StringComparer.Ordinal);
      foreach (var name in assembly.GetManifestResourceNames())
      {
         // A build on Windows puts backslashes in the relative path.
         var path = name.Replace('\\', '/');
         if (!path.StartsWith(ResourcePrefix, StringComparison.Ordinal))
         {
            continue;
         }

         path = path[ResourcePrefix.Length..];
         if (!contentTypes.TryGetContentType(path, out var contentType))
         {
            contentType = "application/octet-stream";
         }

         assets[path] = new UiAsset(ReadResource(assembly, name), contentType, IsHtml: path.EndsWith(".html", StringComparison.OrdinalIgnoreCase));
      }

      return new UiAssets(assets.ToFrozenDictionary(StringComparer.Ordinal));
   }

   /// <summary>Maps the UI files at the root of the site, below every other route.</summary>
   public void MapUi(IEndpointRouteBuilder endpoints) => endpoints.MapMethods("/{**path}", [HttpMethods.Get, HttpMethods.Head], ServeAsync);

   private Task ServeAsync(HttpContext context)
   {
      var path = context.Request.Path.Value?.TrimStart('/') ?? "";
      if (path.Length == 0)
      {
         path = IndexPath;
      }

      if (RequestGuards.IsApiPath(context.Request.Path) || !_assets.TryGetValue(path, out var asset))
      {
         context.Response.StatusCode = StatusCodes.Status404NotFound;
         return Task.CompletedTask;
      }

      var response = context.Response;
      response.ContentType = asset.ContentType;
      var content = asset.Content;
      if (asset.IsHtml)
      {
         // The page carries a nonce that is new on every load, so it must never come from a cache.
         response.Headers.CacheControl = "no-store";
         var html = Encoding.UTF8.GetString(content).Replace(ContentSecurityPolicy.NoncePlaceholder, ContentSecurityPolicy.GetNonce(context), StringComparison.Ordinal);
         content = Encoding.UTF8.GetBytes(html);
      }
      else
      {
         response.Headers.CacheControl = "no-cache";
      }

      response.ContentLength = content.Length;
      return HttpMethods.IsHead(context.Request.Method) ? Task.CompletedTask : response.Body.WriteAsync(content, context.RequestAborted).AsTask();
   }

   private static byte[] ReadResource(Assembly assembly, string name)
   {
      using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"The embedded resource {name} is missing.");
      using var buffer = new MemoryStream();
      stream.CopyTo(buffer);
      return buffer.ToArray();
   }

   private sealed record UiAsset(byte[] Content, string ContentType, bool IsHtml);
}
