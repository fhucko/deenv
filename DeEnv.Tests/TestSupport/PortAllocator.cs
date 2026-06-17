using System.Net;
using System.Net.Sockets;

namespace DeEnv.Tests.TestSupport;

/// <summary>
/// A process-wide free-TCP-port allocator shared by EVERY test that needs a port — the kernel hosts
/// (<see cref="InstanceContext"/>, <see cref="DeEnv.Tests.Steps"/> KernelSteps) and the in-process
/// <see cref="TestInstanceServer"/>.
///
/// The old approach — bind to :0, read the assigned port, RELEASE it, return it — has two flaws under the
/// suite's parallel load: (1) the port is free again the moment it is returned, so a sibling can bind it
/// in the gap before the real server does; and worse, (2) two callers racing the OS can be handed the SAME
/// port. When that happens a browser pointed at a port can reach the WRONG instance (e.g. a designer
/// scenario drove a different instance than the one its assertions read) — a maddening cross-instance flake.
///
/// This hands out each port AT MOST ONCE for the lifetime of the test process (a monotonic cursor + a
/// permanent used-set), and verifies each candidate is actually bindable right now, so no two test
/// components ever share a port. Single-use across the run is fine: the range dwarfs the ports a run needs.
/// </summary>
public static class PortAllocator
{
    private static readonly object Lock = new();
    private static readonly HashSet<int> Used = new();

    // Above the well-known/registered noise and BELOW the OS ephemeral range (Windows default 49152+),
    // so we don't fight the ports that :0 binds elsewhere hand out.
    private const int Min = 20000;
    private const int Max = 48000;
    private static int _cursor = Min;

    /// <summary>A free port not handed out before in this process. Throws if the range is exhausted.</summary>
    public static int Next()
    {
        lock (Lock)
        {
            for (var scanned = 0; scanned <= Max - Min; scanned++)
            {
                var port = _cursor++;
                if (_cursor > Max) _cursor = Min;
                if (!Used.Add(port)) continue;   // already handed out this run
                if (!Bindable(port)) continue;   // taken by something else right now
                return port;
            }
            throw new InvalidOperationException("PortAllocator: no free port available in range.");
        }
    }

    private static bool Bindable(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
