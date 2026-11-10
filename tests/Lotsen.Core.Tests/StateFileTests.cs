using Lotsen.Hosting;

namespace Lotsen.Tests;

/// <summary>Covers how clients read the state file.</summary>
public sealed class StateFileTests : IDisposable
{
   private readonly DataDirectory _dataDirectory = new(Directory.CreateTempSubdirectory("lotsen-tests-").FullName);

   [Fact]
   public void Written_state_reads_back()
   {
      var state = new ServerState(42, "127.0.0.1", 50123, "secret-token");

      StateFile.Write(_dataDirectory, state);

      Assert.Equal(state, StateFile.TryRead(_dataDirectory));
   }

   [Theory]
   [InlineData("""{"pid":1,"address":"127.0.0.1","port":"50123","token":"t"}""")]
   [InlineData("""{"pid":1,"address":"127.0.0""")]
   [InlineData("")]
   public void Malformed_state_reads_as_no_server(string content)
   {
      File.WriteAllText(_dataDirectory.StateFilePath, content);

      Assert.Null(StateFile.TryRead(_dataDirectory));
   }

   /// <summary>A state file that no server writes, whose address or port no client of lotsen may call.</summary>
   [Theory]
   [InlineData("127.0.0.1", 0)]
   [InlineData("127.0.0.1", -1)]
   [InlineData("127.0.0.1", 65536)]
   [InlineData("192.168.1.10", 50123)]
   [InlineData("0.0.0.0", 50123)]
   [InlineData("::1", 50123)]
   [InlineData("example.com", 50123)]
   [InlineData("127.0.0.1/", 50123)]
   public void State_with_a_port_out_of_range_or_an_address_off_loopback_reads_as_no_server(string address, int port)
   {
      StateFile.Write(_dataDirectory, new ServerState(42, address, port, "secret-token"));

      Assert.Null(StateFile.TryRead(_dataDirectory));
   }

   [Theory]
   [InlineData(1)]
   [InlineData(65535)]
   public void State_with_a_port_at_either_end_of_the_range_reads_back(int port)
   {
      var state = new ServerState(42, "127.0.0.1", port, "secret-token");
      StateFile.Write(_dataDirectory, state);

      Assert.Equal(state, StateFile.TryRead(_dataDirectory));
   }

   [Fact]
   public void State_prints_without_its_token()
   {
      var state = new ServerState(42, "127.0.0.1", 50123, "secret-token");

      Assert.DoesNotContain("secret-token", state.ToString());
   }

   public void Dispose() => Directory.Delete(_dataDirectory.Path, recursive: true);
}
