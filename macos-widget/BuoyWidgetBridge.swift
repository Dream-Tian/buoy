import Foundation
import WidgetKit

/// 主程序每次刷新用量后调用它，让 WidgetKit 尽快重取本机接口的数据。
/// 参数保留是为了不改 C# 侧的 P/Invoke 签名，快照本身走 localhost。
@_cdecl("BuoyWidgetPublish")
public func buoyWidgetPublish(_ rawJSON: UnsafePointer<CChar>?) {
    WidgetCenter.shared.reloadTimelines(ofKind: "BuoyUsageWidget")
}
