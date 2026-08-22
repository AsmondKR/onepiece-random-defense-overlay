namespace OrandOverlay;

public static class OverlayVisibilityPolicy
{
    public static bool ShouldShow(RecognitionResult result) =>
        result.State == RecognitionState.Ready &&
        result.ShouldReplaceInventory &&
        result.Entries.Any(entry => entry.Count > 0);
}
