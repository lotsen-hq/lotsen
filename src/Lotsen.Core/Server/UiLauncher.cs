using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lotsen.Operations;
using Lotsen.Tours;

namespace Lotsen.Server;

/// <summary>The input of <c>open_ui</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record OpenUiInput
{
   // The schema of a tour ID has no null, so an input without a tour leaves the property out of its JSON, and the property has no constructor default for the schema to publish.
   [Description("The ID of the tour that the page opens on. Omit it to open the page with no tour selected.")]
   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
   public TourId? TourId { get; init; }
}

/// <summary>The outcome of opening the UI.</summary>
/// <remarks>An unspent one-time code yields the UI credential, so the result carries the link only when no browser took it.</remarks>
internal sealed record OpenUiResult
{
   /// <param name="launched">Whether a browser launcher accepted the link.</param>
   /// <param name="url">The link to the UI when no browser launched, or null when one did.</param>
   /// <exception cref="JsonException">The result pairs a link with a launched browser, or lacks the link that a failed launch leaves.</exception>
   [JsonConstructor]
   private OpenUiResult(bool launched, string? url = null)
   {
      if (launched == url is not null)
      {
         throw new JsonException(launched ? "A launched result must carry no link." : "A result without a launch must carry the link.");
      }

      Launched = launched;
      Url = url;
   }

   /// <summary>Whether a browser launcher accepted the link.</summary>
   public bool Launched { get; }

   /// <summary>
   /// The link that the user must open by hand, carrying a one-time code in its fragment and never a credential, or null when a browser launched. JSON leaves it out when null.
   /// </summary>
   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
   public string? Url { get; }

   /// <summary>Returns the result of a launch that a browser launcher accepted.</summary>
   public static OpenUiResult BrowserLaunched() => new(launched: true);

   /// <summary>Returns the result of a launch that no browser launcher accepted, with the link for the user to open.</summary>
   public static OpenUiResult NotLaunched(string url) => new(launched: false, url);
}

/// <summary>A link to the UI for the user to open by hand.</summary>
/// <param name="Url">The link, carrying a one-time code in its fragment and never a credential.</param>
internal sealed record UiLinkResult(string Url);

/// <summary>Opens the UI in the user's browser with a fresh one-time code, which the page exchanges for the UI credential.</summary>
internal sealed class UiLauncher
{
   private readonly SessionCodes _codes;
   private readonly IBrowserLauncher _browser;
   private readonly ServerEndpoint _endpoint;
   private readonly TourWriter _writer;

   public UiLauncher(SessionCodes codes, IBrowserLauncher browser, ServerEndpoint endpoint, TourWriter writer)
   {
      _codes = codes;
      _browser = browser;
      _endpoint = endpoint;
      _writer = writer;
   }

   /// <summary>Opens the UI, with the tour selected when the input names one.</summary>
   /// <param name="input">The input of <c>open_ui</c>.</param>
   /// <param name="cancellationToken">Cancels the wait for the browser launcher.</param>
   /// <returns>The outcome of the launch, or the refusal of <see cref="TourWriter.Read"/>. A refusal issues no one-time code and launches no browser.</returns>
   public async Task<OperationResult<OpenUiResult>> OpenAsync(OpenUiInput input, CancellationToken cancellationToken)
   {
      if (input.TourId is { } tourId && _writer.Read(tourId).Error is { } refusal)
      {
         return refusal;
      }

      var url = NewLink(input.TourId);
      return await _browser.TryLaunchAsync(url, cancellationToken) ? OpenUiResult.BrowserLaunched() : OpenUiResult.NotLaunched(url);
   }

   /// <summary>Returns a link to the UI with a fresh one-time code, and launches no browser.</summary>
   public UiLinkResult CreateLink() => new(NewLink(tourId: null));

   // The fragment stays in the browser, so the code never reaches a server log or a Referer header.
   private string NewLink(TourId? tourId) => $"http://{_endpoint.BrowserHost}:{_endpoint.Port}/#code={_codes.Issue()}{(tourId is null ? "" : $"&tour={tourId}")}";
}

/// <summary>
/// The loopback endpoint the server listens on, which the server's start builds from the socket it bound before the host starts.
/// </summary>
internal sealed class ServerEndpoint
{
   /// <param name="port">The TCP port of the bound socket.</param>
   public ServerEndpoint(int port)
   {
      Port = port;
   }

   /// <summary>The TCP port the server listens on.</summary>
   public int Port { get; }

   /// <summary>The host name that links for the browser use.</summary>
   /// <remarks>
   /// Under WSL a Windows browser reaches the server through WSL's forwarding of <c>localhost</c>.
   /// </remarks>
   public string BrowserHost { get; } = SystemBrowserLauncher.IsWsl() ? "localhost" : "127.0.0.1";
}
