using System.Net;
using System.Net.Sockets;

namespace Farm.Testing.Shared;

/// <summary>
/// Starts test <see cref="HttpListener"/> servers on a free IPv4 loopback port (#3029).
/// <para>
/// HttpListener cannot bind port 0, so a candidate port has to be chosen up front. Probing a
/// candidate and releasing it leaves a window in which a parallel test host can take the port.
/// This helper closes that window by treating a failed <see cref="HttpListener.Start"/> (port
/// taken by another socket, or the same prefix already registered in this process) as "try
/// another port", and by returning the listener that started still running, so nothing is
/// released between the successful bind and the caller serving on it.
/// </para>
/// </summary>
public static class LoopbackHttpListener
{
    /// <summary>Default number of candidate ports tried before giving up.</summary>
    public const int DefaultMaxAttempts = 20;

    /// <summary>
    /// Starts a listener on <c>http://127.0.0.1:{port}/</c> for a free loopback port.
    /// </summary>
    public static (HttpListener Listener, int Port) Start() =>
        Start(port => $"http://127.0.0.1:{port}/");

    /// <summary>
    /// Starts a listener whose single prefix is built from a free candidate port.
    /// </summary>
    /// <param name="prefixForPort">Builds the listener prefix for a candidate port.</param>
    /// <param name="candidatePorts">
    /// Supplies candidate ports; defaults to <see cref="NextCandidatePort"/>. Injectable so the
    /// retry path can be exercised deterministically.
    /// </param>
    /// <param name="maxAttempts">Maximum number of candidate ports to try.</param>
    /// <returns>The started listener and the port it is serving on.</returns>
    /// <exception cref="AggregateException">Every attempt failed to start; holds each failure.</exception>
    public static (HttpListener Listener, int Port) Start(
        Func<int, string> prefixForPort,
        Func<int>? candidatePorts = null,
        int maxAttempts = DefaultMaxAttempts)
    {
        ArgumentNullException.ThrowIfNull(prefixForPort);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        candidatePorts ??= NextCandidatePort;

        List<Exception> failures = [];
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            int port = candidatePorts();
            HttpListener listener = new();
            bool started = false;
            try
            {
                listener.Prefixes.Add(prefixForPort(port));
                listener.Start();
                started = true;
                return (listener, port);
            }
            catch (Exception ex) when (ex is HttpListenerException or SocketException)
            {
                failures.Add(ex);
            }
            finally
            {
                if (!started)
                {
                    CloseQuietly(listener);
                }
            }
        }

        throw new AggregateException(
            $"HttpListener failed to start on {maxAttempts} candidate loopback ports.",
            failures);
    }

    /// <summary>
    /// Returns an OS-assigned port that was free on IPv4 loopback at the time of the call. The
    /// probe socket is released, so the caller must still treat a failed bind as retryable.
    /// </summary>
    public static int NextCandidatePort()
    {
        using TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    // A cleanup failure must not mask the start failure that is being retried or rethrown.
    private static void CloseQuietly(HttpListener listener)
    {
        try
        {
            listener.Close();
        }
        catch
        {
        }
    }
}
