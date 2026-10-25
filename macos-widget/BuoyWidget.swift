import Foundation
import SwiftUI
import WidgetKit

private let snapshotURL = URL(string: "http://127.0.0.1:47821/usage.json")!

struct UsageModel: Codable {
    let model: String
    let tokens: Int64
    let requests: Int64
    let share: Double
    let costUsd: Double
}

struct UsageSnapshot: Codable {
    let totalTokens: Int64
    let requestCount: Int64
    let averageRpm: Double
    let averageTpm: Double
    let updatedAtUnixSeconds: Int64
    let isConfigured: Bool
    let hasError: Bool
    let topModels: [UsageModel]
    let costUsd: Double
    let rangeLabel: String?
    let quota: QuotaSnapshot?
    var chatgptQuota: ChatGptQuotaSnapshot? = nil
    var trendTokens: [Double]? = nil

    var rangeText: String { rangeLabel ?? "今日" }

    static let empty = UsageSnapshot(
        totalTokens: 0,
        requestCount: 0,
        averageRpm: 0,
        averageTpm: 0,
        updatedAtUnixSeconds: 0,
        isConfigured: false,
        hasError: false,
        topModels: [],
        costUsd: 0,
        rangeLabel: nil,
        quota: nil
    )
}

struct QuotaModelSnapshot: Codable {
    let model: String
    let label: String?
    let remainingFraction: Double?
}

struct ChatGptWindowSnapshot: Codable {
    let usedPercent: Double?
    let windowSeconds: Int64?
    let resetAtUnixSeconds: Int64?
}

struct ChatGptQuotaSnapshot: Codable {
    let message: String
    let planName: String?
    let primaryWindow: ChatGptWindowSnapshot?
    let secondaryWindow: ChatGptWindowSnapshot?
    let updatedAtUnixSeconds: Int64
}

struct QuotaSnapshot: Codable {
    let message: String
    let planName: String?
    let availableCredits: Double?
    let monthlyCredits: Double?
    let resetsAtUnixSeconds: Int64?
    let updatedAtUnixSeconds: Int64
    let models: [QuotaModelSnapshot]
}

struct UsageEntry: TimelineEntry {
    let date: Date
    let snapshot: UsageSnapshot
    var fromCache: Bool = false
}

struct UsageProvider: TimelineProvider {
    func placeholder(in context: Context) -> UsageEntry {
        UsageEntry(date: Date(), snapshot: UsageSnapshot(
            totalTokens: 128_400,
            requestCount: 42,
            averageRpm: 3.2,
            averageTpm: 9_860,
            updatedAtUnixSeconds: Int64(Date().timeIntervalSince1970),
            isConfigured: true,
            hasError: false,
            topModels: [
                UsageModel(model: "claude-sonnet-4-5", tokens: 53_748, requests: 18, share: 0.42, costUsd: 5.12),
                UsageModel(model: "gpt-5-codex", tokens: 39_804, requests: 14, share: 0.31, costUsd: 3.78),
                UsageModel(model: "deepseek-v3.2", tokens: 34_848, requests: 10, share: 0.27, costUsd: 3.44)
            ],
            costUsd: 12.34,
            rangeLabel: "今日",
            quota: nil
        ))
    }

    func getSnapshot(in context: Context, completion: @escaping (UsageEntry) -> Void) {
        loadBuoySnapshot { snapshot, fromCache in
            completion(UsageEntry(date: Date(), snapshot: snapshot, fromCache: fromCache))
        }
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<UsageEntry>) -> Void) {
        loadBuoySnapshot { snapshot, fromCache in
            let now = Date()
            let nextRefresh = Calendar.current.date(byAdding: .minute, value: 5, to: now) ?? now.addingTimeInterval(300)
            completion(Timeline(entries: [UsageEntry(date: now, snapshot: snapshot, fromCache: fromCache)], policy: .after(nextRefresh)))
        }
    }

}

private let snapshotFileURL = URL(fileURLWithPath: "/Users/\(NSUserName())/Library/Application Support/Buoy/widget-snapshot.json")

/// 从主程序在本机起的只读接口取数；主程序没开时退回上次落盘的快照。
private func loadBuoySnapshot(completion: @escaping (UsageSnapshot, Bool) -> Void) {
    var request = URLRequest(url: snapshotURL)
    request.timeoutInterval = 2
    request.cachePolicy = .reloadIgnoringLocalCacheData
    URLSession.shared.dataTask(with: request) { data, response, _ in
        if let data, let live = try? JSONDecoder().decode(UsageSnapshot.self, from: data) {
            DispatchQueue.main.async { completion(live, false) }
            return
        }

        let cached = loadCachedBuoySnapshot()
        DispatchQueue.main.async { completion(cached, true) }
    }.resume()
}

private func loadCachedBuoySnapshot() -> UsageSnapshot {
    do {
        let data = try Data(contentsOf: snapshotFileURL)
        return try JSONDecoder().decode(UsageSnapshot.self, from: data)
    } catch {
        NSLog("Buoy widget could not read the cached snapshot: %@", String(describing: error))
        return .empty
    }
}

/// 所有组件共用的圆角卡片底：浅色填充 + 发丝边框，贴着系统材质形成“贴纸”质感。
extension View {
    func widgetCard() -> some View {
        self
            .padding(.horizontal, 16)
            .padding(.vertical, 14)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(
                RoundedRectangle(cornerRadius: 19, style: .continuous)
                    .fill(Color.primary.opacity(0.045))
            )
            .overlay(
                RoundedRectangle(cornerRadius: 19, style: .continuous)
                    .strokeBorder(Color.primary.opacity(0.09), lineWidth: 1)
            )
            .padding(4)
    }
}

struct UsageWidgetView: View {
    let entry: UsageEntry
    @Environment(\.widgetFamily) private var family

    var body: some View {
        Group {
            if family == .systemMedium {
                medium
            } else {
                small
            }
        }
        .widgetCard()
    }

    private var small: some View {
        VStack(spacing: 10) {
            VStack(spacing: 2) {
                Text(valueText)
                    .font(.system(size: 33, weight: .semibold, design: .rounded))
                    .monospacedDigit()
                    .minimumScaleFactor(0.6)
                    .lineLimit(1)
                Text(captionText)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
            }
            HStack(alignment: .top, spacing: 14) {
                centeredMetric("请求", formatCount(entry.snapshot.requestCount))
                centeredMetric("RPM", formatRate(entry.snapshot.averageRpm))
                centeredMetric("TPM", formatRate(entry.snapshot.averageTpm))
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .center)
    }

    private func centeredMetric(_ title: String, _ value: String) -> some View {
        VStack(spacing: 2) {
            Text(value)
                .font(.system(size: 15, weight: .semibold))
                .monospacedDigit()
                .lineLimit(1)
                .minimumScaleFactor(0.7)
            Text(title)
                .font(.system(size: 10))
                .foregroundStyle(.secondary)
        }
    }

    private var medium: some View {
        HStack(alignment: .top, spacing: 14) {
            VStack(alignment: .leading, spacing: 4) {
                Text(valueText)
                    .font(.system(size: 28, weight: .semibold, design: .rounded))
                    .monospacedDigit()
                    .minimumScaleFactor(0.6)
                    .lineLimit(1)
                Text(captionText)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)

                Spacer(minLength: 6)

                if let trend = entry.snapshot.trendTokens, trend.contains(where: { $0 > 0 }) {
                    UsageSparkline(values: trend)
                        .frame(height: 32)
                } else if entry.snapshot.isConfigured {
                    Text("今日暂无走势")
                        .font(.system(size: 9))
                        .foregroundStyle(.secondary)
                        .frame(height: 32, alignment: .center)
                        .frame(maxWidth: .infinity)
                }
                Text(modelSummaryText)
                    .font(.system(size: 9))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
            }

            VStack(alignment: .trailing, spacing: 7) {
                inlineMetric("请求", formatCount(entry.snapshot.requestCount))
                inlineMetric("RPM", formatRate(entry.snapshot.averageRpm))
                inlineMetric("TPM", formatRate(entry.snapshot.averageTpm))
                Spacer(minLength: 6)
                if let top = entry.snapshot.topModels.first {
                    Text(top.model)
                        .font(.system(size: 10, weight: .medium))
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Text(modelDetail(top))
                        .font(.system(size: 9))
                        .foregroundStyle(.secondary)
                        .monospacedDigit()
                        .lineLimit(1)
                }
            }
            .fixedSize(horizontal: true, vertical: false)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
    }

    private func inlineMetric(_ title: String, _ value: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 5) {
            Text(value)
                .font(.system(size: 13, weight: .semibold))
                .monospacedDigit()
                .lineLimit(1)
            Text(title)
                .font(.system(size: 9))
                .foregroundStyle(.secondary)
        }
    }

    private var modelSummaryText: String {
        let count = entry.snapshot.topModels.count
        let models = count == 0 ? "暂无模型" : "共 \(count) 个模型"
        let text = "\(models) · \(entry.snapshot.rangeText)消耗 \(formatCost(entry.snapshot.costUsd))"
        guard entry.fromCache else { return text }
        let time = QuotaText.time(entry.snapshot.updatedAtUnixSeconds)
        return time.isEmpty ? "\(text) · 缓存" : "\(text) · 缓存 \(time)"
    }

    private func metric(_ title: String, _ value: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(value)
                .font(.system(size: 15, weight: .semibold))
                .monospacedDigit()
                .lineLimit(1)
                .minimumScaleFactor(0.8)
            Text(title)
                .font(.system(size: 10))
                .foregroundStyle(.secondary)
        }
    }

    private var valueText: String {
        entry.snapshot.isConfigured ? formatCount(entry.snapshot.totalTokens) : "--"
    }

    private var captionText: String {
        if entry.snapshot.hasError { return "刷新失败" }
        if entry.snapshot.updatedAtUnixSeconds == 0 { return "打开 Buoy 同步数据" }
        if !entry.snapshot.isConfigured { return "在 Buoy 里配置 NewAPI" }
        let base = "\(entry.snapshot.rangeText) TOKEN"
        guard entry.fromCache, family != .systemMedium else { return base }
        return "\(base) · 缓存"
    }

    private func percent(_ share: Double) -> String {
        "\(Int((share * 100).rounded()))%"
    }

    private func modelDetail(_ model: UsageModel) -> String {
        model.costUsd > 0
            ? "\(percent(model.share)) · \(formatCost(model.costUsd))"
            : percent(model.share)
    }

    private func formatCost(_ value: Double) -> String {
        if value >= 1_000 { return String(format: "$%.0f", value) }
        if value >= 1 { return String(format: "$%.2f", value) }
        if value >= 0.01 { return String(format: "$%.3f", value) }
        return String(format: "$%.4f", value)
    }

    private func formatCount(_ value: Int64) -> String {
        if value >= 100_000_000 {
            return String(format: "%.2f亿", Double(value) / 100_000_000)
        }
        if value >= 10_000 {
            return String(format: "%.2f万", Double(value) / 10_000)
        }
        return value.formatted(.number)
    }

    private func formatRate(_ value: Double) -> String {
        if value >= 10_000 {
            return String(format: "%.1f万", value / 10_000)
        }
        if value >= 100 {
            return String(format: "%.0f", value)
        }
        return String(format: "%.2f", value)
            .replacingOccurrences(of: "0+$", with: "", options: .regularExpression)
            .replacingOccurrences(of: "\\.$", with: "", options: .regularExpression)
    }
}

struct QuotaEntry: TimelineEntry {
    let date: Date
    let snapshot: UsageSnapshot
    var fromCache: Bool = false
}

struct QuotaProvider: TimelineProvider {
    func placeholder(in context: Context) -> QuotaEntry {
        QuotaEntry(date: Date(), snapshot: .quotaPreview)
    }

    func getSnapshot(in context: Context, completion: @escaping (QuotaEntry) -> Void) {
        loadBuoySnapshot { snapshot, fromCache in
            completion(QuotaEntry(date: Date(), snapshot: snapshot, fromCache: fromCache))
        }
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<QuotaEntry>) -> Void) {
        loadBuoySnapshot { snapshot, fromCache in
            let now = Date()
            let nextRefresh = Calendar.current.date(byAdding: .minute, value: 5, to: now) ?? now.addingTimeInterval(300)
            completion(Timeline(entries: [QuotaEntry(date: now, snapshot: snapshot, fromCache: fromCache)], policy: .after(nextRefresh)))
        }
    }
}

extension UsageSnapshot {
    static let quotaPreview = UsageSnapshot(
        totalTokens: 0,
        requestCount: 0,
        averageRpm: 0,
        averageTpm: 0,
        updatedAtUnixSeconds: Int64(Date().timeIntervalSince1970),
        isConfigured: true,
        hasError: false,
        topModels: [],
        costUsd: 0,
        rangeLabel: "今日",
        quota: QuotaSnapshot(
            message: "",
            planName: "Pro",
            availableCredits: 120,
            monthlyCredits: 250,
            resetsAtUnixSeconds: Int64(Date().timeIntervalSince1970) + 5400,
            updatedAtUnixSeconds: Int64(Date().timeIntervalSince1970),
            models: [
                QuotaModelSnapshot(model: "gemini-3-pro", label: "Gemini 3 Pro", remainingFraction: 0.72),
                QuotaModelSnapshot(model: "claude-sonnet-4-5", label: "Claude Sonnet 4.5", remainingFraction: 0.41),
                QuotaModelSnapshot(model: "gpt-5", label: "GPT-5", remainingFraction: 0.18)
            ]))
}

struct QuotaGroupSnapshot: Identifiable {
    let title: String
    let shortTitle: String
    let modelCount: Int
    let fraction: Double

    var id: String { title }
}

enum QuotaText {
    static func percent(_ fraction: Double) -> String {
        "\(Int((max(0, min(1, fraction)) * 100).rounded()))%"
    }

    static func time(_ seconds: Int64?) -> String {
        guard let seconds, seconds > 0 else { return "" }
        let formatter = DateFormatter()
        formatter.timeStyle = .short
        formatter.dateStyle = .none
        return formatter.string(from: Date(timeIntervalSince1970: TimeInterval(seconds)))
    }

    static func reset(_ seconds: Int64?) -> String {
        let short = countdownShort(seconds)
        return short.isEmpty ? "" : short + " 后重置"
    }

    /// 倒计时短格式：<1h “34m”、<24h “5h12m”、更长给日期 “11/20”。
    static func countdownShort(_ seconds: Int64?) -> String {
        guard let seconds else { return "" }
        let remain = seconds - Int64(Date().timeIntervalSince1970)
        guard remain > 0 else { return "" }
        if remain < 3600 { return "\(max(1, remain / 60))m" }
        if remain < 86_400 { return "\(remain / 3600)h\(remain % 3600 / 60)m" }
        let formatter = DateFormatter()
        formatter.dateFormat = "MM/dd"
        return formatter.string(from: Date(timeIntervalSince1970: TimeInterval(seconds)))
    }

    /// 长窗口（周/月）的重置显示日期，短窗口显示倒计时。
    static func resetAdaptive(_ seconds: Int64?, windowSeconds: Int64?) -> String {
        guard let seconds else { return "" }
        if windowSeconds ?? 0 >= 172_800 {
            let formatter = DateFormatter()
            formatter.dateFormat = "MM/dd"
            return formatter.string(from: Date(timeIntervalSince1970: TimeInterval(seconds))) + " 重置"
        }
        return reset(seconds)
    }
}

struct RingMeter: View {
    let fraction: Double
    var lineWidth: CGFloat = 4

    private var ringColor: Color {
        if fraction > 0.5 { return .green }
        if fraction > 0.2 { return .orange }
        return .red
    }

    var body: some View {
        ZStack {
            Circle()
                .stroke(Color.primary.opacity(0.09), lineWidth: lineWidth)
            Circle()
                .trim(from: 0, to: max(0.02, min(1, fraction)))
                .stroke(ringColor, style: StrokeStyle(lineWidth: lineWidth, lineCap: .round))
                .rotationEffect(.degrees(-90))
        }
    }
}

struct QuotaRing: View {
    let fraction: Double
    var size: CGFloat = 44
    var lineWidth: CGFloat = 5
    var fontSize: CGFloat = 11

    var body: some View {
        ZStack {
            RingMeter(fraction: fraction, lineWidth: lineWidth)
            Text(QuotaText.percent(fraction))
                .font(.system(size: fontSize, weight: .semibold, design: .rounded))
                .monospacedDigit()
        }
        .frame(width: size, height: size)
    }
}

/// 五段式电量条：每段 20%，跟圆环组件形成区分。
struct SegmentedBar: View {
    let fraction: Double
    var segmentCount: Int = 5
    var height: CGFloat = 7

    private var fillColor: Color {
        if fraction > 0.5 { return .green }
        if fraction > 0.2 { return .orange }
        return .red
    }

    var body: some View {
        let value = max(0, min(1, fraction))
        let filled = value > 0.95 ? segmentCount : Int((value * Double(segmentCount)).rounded(.down))
        HStack(spacing: 3) {
            ForEach(0..<segmentCount, id: \.self) { index in
                Capsule()
                    .fill(index < filled ? fillColor : Color.primary.opacity(0.12))
            }
        }
        .frame(height: height)
    }
}

/// 今日每小时 Token 的面积迷你图，用在用量组件的 medium 尺寸。
struct UsageSparkline: View {
    let values: [Double]
    var color: Color = .blue

    var body: some View {
        GeometryReader { geo in
            let peak = Swift.max(values.max() ?? 1, 1)
            let step = geo.size.width / CGFloat(Swift.max(values.count - 1, 1))
            let points = values.enumerated().map { index, value in
                CGPoint(
                    x: CGFloat(index) * step,
                    y: geo.size.height * (1 - CGFloat(value / peak) * 0.92))
            }
            if let first = points.first, points.count > 1 {
                let area = Path { path in
                    path.move(to: CGPoint(x: first.x, y: geo.size.height))
                    points.forEach { path.addLine(to: $0) }
                    path.addLine(to: CGPoint(x: points[points.count - 1].x, y: geo.size.height))
                    path.closeSubpath()
                }
                let line = Path { path in
                    path.move(to: points[0])
                    points.dropFirst().forEach { path.addLine(to: $0) }
                }
                ZStack {
                    area.fill(color.opacity(0.2))
                    line.stroke(color, style: StrokeStyle(lineWidth: 1.8, lineCap: .round, lineJoin: .round))
                }
            }
        }
    }
}

struct QuotaWidgetView: View {
    let entry: QuotaEntry
    @Environment(\.widgetFamily) private var family

    private var quota: QuotaSnapshot? { entry.snapshot.quota }

    private var groups: [QuotaGroupSnapshot] {
        // 已下线模型（如 gemini-4-argon-disabled）的 remainingFraction 恒为 0，
        // 主程序修复前的缓存文件里可能还留着，这里一并剔除。
        let models = (quota?.models ?? []).filter { !$0.model.lowercased().hasSuffix("-disabled") }
        let gemini = models.filter { $0.model.lowercased().hasPrefix("gemini") }
        let others = models.filter {
            let name = $0.model.lowercased()
            return name.hasPrefix("claude") || name.hasPrefix("gpt")
        }

        var result: [QuotaGroupSnapshot] = []
        if let fraction = worstFraction(gemini) {
            result.append(QuotaGroupSnapshot(title: "Gemini 模型", shortTitle: "Gemini", modelCount: gemini.count, fraction: fraction))
        }
        if let fraction = worstFraction(others) {
            result.append(QuotaGroupSnapshot(title: "Claude 和 GPT 模型", shortTitle: "Claude · GPT", modelCount: others.count, fraction: fraction))
        }
        if result.isEmpty, let fraction = worstFraction(models) {
            result.append(QuotaGroupSnapshot(title: "剩余额度", shortTitle: "剩余", modelCount: models.count, fraction: fraction))
        }
        return result
    }

    var body: some View {
        Group {
            if family == .systemMedium {
                medium
            } else {
                small
            }
        }
        .widgetCard()
    }

    private var small: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text("配额")
                    .font(.system(size: 10, weight: .semibold))
                Spacer(minLength: 4)
                Text(quota?.planName ?? "Antigravity")
                    .font(.system(size: 9))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }

            Spacer(minLength: 6)

            if groups.isEmpty {
                Text(statusText)
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
                    .lineLimit(3)
                Spacer(minLength: 0)
            } else {
                // 单环：取所有模型里最紧的一档。
                VStack(spacing: 6) {
                    QuotaRing(fraction: tightestFraction, size: 52, lineWidth: 6, fontSize: 13)
                    Text("各模型最低")
                        .font(.system(size: 9))
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .minimumScaleFactor(0.8)
                }
                .frame(maxWidth: .infinity)

                Spacer(minLength: 4)

                if !footerText.isEmpty {
                    Text(footerText)
                        .font(.system(size: 8))
                        .foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, alignment: .center)
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    private var medium: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text("Antigravity 配额")
                    .font(.system(size: 11, weight: .semibold))
                Spacer(minLength: 6)
                Text(headerDetailText)
                    .font(.system(size: 9))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }

            Spacer(minLength: 0)

            if groups.isEmpty {
                Text(statusText)
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
                Spacer(minLength: 0)
            } else {
                VStack(spacing: 13) {
                    ForEach(Array(groups.prefix(2))) { group in
                        VStack(spacing: 5) {
                            HStack(alignment: .firstTextBaseline) {
                                Text(group.title)
                                    .font(.system(size: 10.5, weight: .medium))
                                    .lineLimit(1)
                                    .minimumScaleFactor(0.8)
                                Spacer(minLength: 8)
                                Text(QuotaText.percent(group.fraction))
                                    .font(.system(size: 11, weight: .semibold))
                                    .monospacedDigit()
                            }
                            SegmentedBar(fraction: group.fraction, height: 9)
                        }
                    }
                }
                Spacer(minLength: 0)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    private var tightestFraction: Double {
        groups.map { $0.fraction }.min() ?? 0
    }

    private func worstFraction(_ models: [QuotaModelSnapshot]) -> Double? {
        models.compactMap { $0.remainingFraction }.min()
    }

    private var headerDetailText: String {
        var parts: [String] = []
        if let plan = quota?.planName, !plan.isEmpty {
            parts.append(plan)
        }
        let reset = QuotaText.reset(quota?.resetsAtUnixSeconds)
        if !reset.isEmpty {
            parts.append(reset)
        }
        if entry.fromCache {
            parts.append("缓存")
        }
        return parts.joined(separator: " · ")
    }

    private var footerText: String {
        if entry.fromCache {
            let time = QuotaText.time(quota?.updatedAtUnixSeconds)
            return time.isEmpty ? "缓存数据" : "缓存 \(time)"
        }
        return QuotaText.reset(quota?.resetsAtUnixSeconds)
    }

    private var statusText: String {
        guard let quota else { return "在 Buoy 设置里启用本地 Antigravity" }
        if quota.models.isEmpty {
            return quota.message.isEmpty ? "还没有读到配额" : quota.message
        }
        return quota.message
    }
}

struct ChatGptEntry: TimelineEntry {
    let date: Date
    let snapshot: UsageSnapshot
    var fromCache: Bool = false
}

struct ChatGptProvider: TimelineProvider {
    func placeholder(in context: Context) -> ChatGptEntry {
        ChatGptEntry(date: Date(), snapshot: .chatGptPreview)
    }

    func getSnapshot(in context: Context, completion: @escaping (ChatGptEntry) -> Void) {
        loadBuoySnapshot { snapshot, fromCache in
            completion(ChatGptEntry(date: Date(), snapshot: snapshot, fromCache: fromCache))
        }
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<ChatGptEntry>) -> Void) {
        loadBuoySnapshot { snapshot, fromCache in
            let now = Date()
            let nextRefresh = Calendar.current.date(byAdding: .minute, value: 5, to: now) ?? now.addingTimeInterval(300)
            completion(Timeline(entries: [ChatGptEntry(date: now, snapshot: snapshot, fromCache: fromCache)], policy: .after(nextRefresh)))
        }
    }
}

extension UsageSnapshot {
    static let chatGptPreview = UsageSnapshot(
        totalTokens: 0,
        requestCount: 0,
        averageRpm: 0,
        averageTpm: 0,
        updatedAtUnixSeconds: Int64(Date().timeIntervalSince1970),
        isConfigured: true,
        hasError: false,
        topModels: [],
        costUsd: 0,
        rangeLabel: "今日",
        quota: nil,
        chatgptQuota: ChatGptQuotaSnapshot(
            message: "",
            planName: "Plus",
            primaryWindow: ChatGptWindowSnapshot(usedPercent: 32, windowSeconds: 18000, resetAtUnixSeconds: Int64(Date().timeIntervalSince1970) + 4500),
            secondaryWindow: ChatGptWindowSnapshot(usedPercent: 61, windowSeconds: 604800, resetAtUnixSeconds: Int64(Date().timeIntervalSince1970) + 86400),
            updatedAtUnixSeconds: Int64(Date().timeIntervalSince1970)))
}

private struct ChatGptWindowSlot: Identifiable {
    let window: ChatGptWindowSnapshot
    let label: String
    let reset: String

    var id: String { label }
}

/// 官方 ChatGPT 登录的额度：wham/usage 的 5 小时窗口和每周窗口，按剩余比例显示。
struct ChatGptWidgetView: View {
    let entry: ChatGptEntry
    @Environment(\.widgetFamily) private var family

    private var quota: ChatGptQuotaSnapshot? { entry.snapshot.chatgptQuota }

    private var windows: [ChatGptWindowSlot] {
        guard let quota else { return [] }
        return [quota.primaryWindow, quota.secondaryWindow].compactMap { window in
            guard let window else { return nil }
            return ChatGptWindowSlot(
                window: window,
                label: windowLabel(window.windowSeconds),
                reset: QuotaText.resetAdaptive(window.resetAtUnixSeconds, windowSeconds: window.windowSeconds))
        }
    }

    private func windowLabel(_ seconds: Int64?) -> String {
        if let seconds, seconds >= 2_160_000 { return "30 天" }
        if let seconds, seconds >= 172_800 { return "每周" }
        if let seconds, seconds >= 3_600 { return "5 小时" }
        return "当前"
    }

    /// wham/usage 给的是已用百分比，小组件跟 Antigravity 一样展示剩余比例。
    private func remainingFraction(_ window: ChatGptWindowSnapshot?) -> Double {
        let used = max(0, min(100, window?.usedPercent ?? 0))
        return (100 - used) / 100
    }

    var body: some View {
        Group {
            if family == .systemMedium {
                medium
            } else {
                small
            }
        }
        .widgetCard()
    }

    private var small: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text("ChatGPT")
                    .font(.system(size: 10, weight: .semibold))
                Spacer(minLength: 4)
                Text(quota?.planName ?? "官方登录")
                    .font(.system(size: 9))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
            }

            Spacer(minLength: 8)

            if windows.isEmpty {
                Text(statusText)
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
                    .lineLimit(3)
                Spacer(minLength: 0)
            } else {
                // 单环：取更紧（剩余更少）的窗口。
                VStack(spacing: 6) {
                    QuotaRing(fraction: remainingFraction(tightestWindow), size: 52, lineWidth: 6, fontSize: 13)
                    Text(tightestWindow.map { windowLabel($0.windowSeconds) } ?? "")
                        .font(.system(size: 9))
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
                .frame(maxWidth: .infinity)

                Spacer(minLength: 4)

                if !footerText.isEmpty {
                    Text(footerText)
                        .font(.system(size: 8))
                        .foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, alignment: .center)
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    private var medium: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text("ChatGPT 配额")
                    .font(.system(size: 11, weight: .semibold))
                Spacer(minLength: 6)
                Text(headerDetailText)
                    .font(.system(size: 9))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
            }

            Spacer(minLength: 0)

            if windows.isEmpty {
                Text(statusText)
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
                Spacer(minLength: 0)
            } else {
                VStack(spacing: 13) {
                    ForEach(windows.prefix(2)) { slot in
                        VStack(spacing: 5) {
                            HStack(alignment: .firstTextBaseline) {
                                Text(slot.label)
                                    .font(.system(size: 10.5, weight: .medium))
                                Spacer(minLength: 8)
                                Text(QuotaText.percent(remainingFraction(slot.window)))
                                    .font(.system(size: 11, weight: .semibold))
                                    .monospacedDigit()
                            }
                            SegmentedBar(fraction: remainingFraction(slot.window), height: 9)
                            if !slot.reset.isEmpty {
                                Text(slot.reset)
                                    .font(.system(size: 9))
                                    .foregroundStyle(.secondary)
                                    .frame(maxWidth: .infinity, alignment: .trailing)
                            }
                        }
                    }
                }
                Spacer(minLength: 0)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    // 倒计时和图例都取更紧（剩余更少）的一侧。
    private var tightestWindow: ChatGptWindowSnapshot? {
        let candidates = [quota?.primaryWindow, quota?.secondaryWindow].compactMap { $0 }
        return candidates.min { remainingFraction($0) < remainingFraction($1) }
    }

    private var headerDetailText: String {
        var parts: [String] = []
        if let plan = quota?.planName, !plan.isEmpty {
            parts.append(plan)
        }
        if entry.fromCache {
            parts.append("缓存")
        }
        return parts.joined(separator: " · ")
    }

    private var footerText: String {
        if entry.fromCache {
            let time = QuotaText.time(quota?.updatedAtUnixSeconds)
            return time.isEmpty ? "缓存数据" : "缓存 \(time)"
        }
        return QuotaText.reset(tightestWindow?.resetAtUnixSeconds)
    }

    private var statusText: String {
        guard let quota else { return "在 Codex 里登录 ChatGPT 后，Buoy 可读取额度" }
        return quota.message.isEmpty ? "还没有读到 ChatGPT 额度" : quota.message
    }
}

struct BuoyChatGptWidget: Widget {
    let kind = "BuoyChatGptWidget"

    var body: some WidgetConfiguration {
        StaticConfiguration(kind: kind, provider: ChatGptProvider()) { entry in
            ChatGptWidgetView(entry: entry)
                .widgetURL(URL(string: "buoy://quota"))
        }
        .configurationDisplayName("Buoy ChatGPT 配额")
        .description("查看 ChatGPT 官方登录的 5 小时和每周剩余额度。")
        .supportedFamilies([.systemSmall, .systemMedium])
        .contentMarginsDisabled()
    }
}

struct BuoyQuotaWidget: Widget {
    let kind = "BuoyQuotaWidget"

    var body: some WidgetConfiguration {
        StaticConfiguration(kind: kind, provider: QuotaProvider()) { entry in
            QuotaWidgetView(entry: entry)
                .widgetURL(URL(string: "buoy://quota"))
        }
        .configurationDisplayName("Buoy 配额")
        .description("查看 Antigravity 各模型剩余配额。")
        .supportedFamilies([.systemSmall, .systemMedium])
        .contentMarginsDisabled()
    }
}

struct BuoyUsageWidget: Widget {
    let kind = "BuoyUsageWidget"

    var body: some WidgetConfiguration {
        StaticConfiguration(kind: kind, provider: UsageProvider()) { entry in
            UsageWidgetView(entry: entry)
                .widgetURL(URL(string: "buoy://show"))
        }
        .configurationDisplayName("Buoy 用量")
        .description("查看今日 Token、请求数和速率。")
        .supportedFamilies([.systemSmall, .systemMedium])
        .contentMarginsDisabled()
    }
}

@main
@MainActor
struct BuoyWidgetBundle: WidgetBundle {
    var body: some Widget {
        BuoyUsageWidget()
        BuoyQuotaWidget()
        BuoyChatGptWidget()
    }
}
