namespace CellPort.Core.Models;

/// <summary>
/// eUICC 可读性探测结论。
///
/// 区分「卡片问题」与「模块通道问题」是必要的 —— 两者现象相似（都读不到 Profile），
/// 但责任方与解决方式完全不同：
///   · ApduLengthLimited → 模块固件的 AT+CSIM 透传长度受限，换卡也没用；
///   · IsdrNotFound      → 通道没问题，是卡片没有 ISD-R 应用（非 eUICC 卡）。
/// </summary>
public enum EuiccCapability
{
    /// <summary>尚未探测。</summary>
    Unknown = 0,

    /// <summary>AT+CSIM 通道本身无响应，无法进行任何判断。</summary>
    ChannelUnavailable,

    /// <summary>
    /// 模块的 AT+CSIM 透传对单条 APDU 有长度上限（约 8 字节），
    /// 无法送达 SELECT ISD-R 所需的 16 字节 AID。
    /// 这是模块固件的限制，与卡片是否为 eUICC 无关。
    /// </summary>
    ApduLengthLimited,

    /// <summary>通道可用，但未能选中 ISD-R：卡片大概率不含 eUICC 应用。</summary>
    IsdrNotFound,

    /// <summary>通道可用且 ISD-R 已选中，可以正常读取 EID 与 Profile。</summary>
    Ready,
}
