namespace MemGuardian.Next.Windows.Native;

/// <summary>集中定义本机 API 权限、PDH 状态和英文 counter path。</summary>
internal static class NativeConstants
{
    internal const uint ProcessSetQuota = 0x0100;
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint TokenQuery = 0x0008;
    internal const uint PdhFormatDouble = 0x00000200;
    internal const uint PdhStatusValidData = 0;
    internal const uint PdhStatusNewData = 1;
    internal const int ErrorSuccess = 0;
    internal const string PageReadsCounter = @"\Memory\Page Reads/sec";
    internal const string PagesInputCounter = @"\Memory\Pages Input/sec";
    internal const string PagefileUsageCounter = @"\Paging File(_Total)\% Usage";
}
