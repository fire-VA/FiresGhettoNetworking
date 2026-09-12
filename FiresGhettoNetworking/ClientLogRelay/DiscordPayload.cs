namespace VerdantsAscent.Modules.ClientLogRelay
{
    /// <summary>
    /// Discord payload limits and byte formatting shared by every relay consumer.
    /// </summary>
    internal static class DiscordPayload
    {
        private const long BytesPerUnit = 1024;
        private const int WebhookAttachmentCeilingBytes = 8 * 1024 * 1024;
        private const int MultipartOverheadBytes = 64 * 1024;

        /// <summary>Largest file a webhook will accept once multipart framing is accounted for.</summary>
        public const int MaxAttachmentBytes = WebhookAttachmentCeilingBytes - MultipartOverheadBytes;

        private static readonly string[] Units = { "B", "KB", "MB", "GB" };

        public static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";

            double scaled = bytes;
            int unit = 0;
            while (scaled >= BytesPerUnit && unit < Units.Length - 1)
            {
                scaled /= BytesPerUnit;
                unit++;
            }
            return $"{scaled:0.##} {Units[unit]}";
        }
    }
}
