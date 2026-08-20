namespace SparkVault.Core.Tests;

internal static class DockerTestHelper
{
    public static bool IsReachable(string host, int port, int timeoutMs = 500)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var connectTask = client.ConnectAsync(host, port);
            return connectTask.Wait(timeoutMs) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
