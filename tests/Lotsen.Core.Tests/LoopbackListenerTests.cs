using System.Net;
using Lotsen.Hosting;

namespace Lotsen.Tests;

/// <summary>Covers the ports that a server accepts and binds: never one that browsers refuse.</summary>
[Trait("Category", "Lifecycle")]
public sealed class LoopbackListenerTests
{
   [Theory]
   [InlineData("6000")]
   [InlineData("6665")]
   [InlineData("10080")]
   [InlineData("1")]
   public void Configured_port_that_browsers_refuse_is_not_a_server_port(string text)
   {
      Assert.False(ServerPort.TryParse(text, out _));
      Assert.True(ServerPort.IsBlockedNumber(text));
   }

   [Theory]
   [InlineData("6001")]
   [InlineData("8080")]
   [InlineData("65535")]
   public void Configured_port_that_browsers_allow_is_a_server_port(string text)
   {
      Assert.True(ServerPort.TryParse(text, out var port));
      Assert.Equal(int.Parse(text, System.Globalization.CultureInfo.InvariantCulture), port.Value);
      Assert.False(ServerPort.IsBlockedNumber(text));
   }

   /// <summary>The fake binds stand for an OS that assigns two blocked ports first.</summary>
   [Fact]
   public void Bind_without_a_port_skips_blocked_ports_and_holds_each_until_it_has_an_allowed_one()
   {
      var events = new List<string>();
      var ports = new Queue<int>([6000, 6665, 50000]);

      var bound = LoopbackListener.Unblocked(
         () =>
         {
            var port = ports.Dequeue();
            events.Add($"bind {port}");
            return new FakeBind(port, events);
         },
         bind => bind.Port);

      Assert.Equal(50000, bound.Port);
      Assert.Equal(["bind 6000", "bind 6665", "bind 50000", "dispose 6000", "dispose 6665"], events);
   }

   [Fact]
   public void Bind_without_a_port_returns_a_socket_on_127_0_0_1_at_an_allowed_port()
   {
      using var socket = LoopbackListener.Bind(null);

      var endpoint = Assert.IsType<IPEndPoint>(socket.LocalEndPoint);
      Assert.Equal(IPAddress.Loopback, endpoint.Address);
      Assert.False(ServerPort.IsBlocked(endpoint.Port));
   }

   private sealed record FakeBind(int Port, List<string> Events) : IDisposable
   {
      public void Dispose() => Events.Add($"dispose {Port}");
   }
}
