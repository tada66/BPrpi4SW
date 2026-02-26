internal static class Tracker
{
    internal class AxisPositions
    {
        internal int XArcsecs;
        internal int YArcsecs;
        internal int ZArcsecs;
    }

    internal static async Task<AxisPositions> GetAxisPositions()
    {
        Logger.Debug("Fetching axis positions... this will likely take a few seconds");
        var pos = new AxisPositions();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await UartClient.Client.PauseMotors();
        System.Action<int, int, int> handler = (x, y, z) =>
        {
            pos.XArcsecs = x;
            pos.YArcsecs = y;
            pos.ZArcsecs = z;
            tcs.TrySetResult(true);
        };

        UartClient.Client.PositionReceived += handler;
        try
        {
            await UartClient.Client.GetPositions();
            await tcs.Task;
        }
        finally
        {
            UartClient.Client.PositionReceived -= handler;
        }
        return pos;
    }
    
    internal static void printPos()
    {
        var pos = GetAxisPositions().Result;
        Logger.Info($"Current Positions - X: {pos.XArcsecs} arcsecs, Y: {pos.YArcsecs} arcsecs, Z: {pos.ZArcsecs} arcsecs");
    }
}