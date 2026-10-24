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

struct QuotaSnapshot: Codable {
    let message: String
    let planName: String?
    let availableCredits: Double?
    let monthlyCredits: Double?
    let updatedAtUnixSeconds: Int64
    let models: [QuotaModelSnapshot]
}

struct UsageEntry: TimelineEntry {
    let date: Date
    let snapshot: UsageSnapshot
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
        loadBuoySnapshot { snapshot in
            completion(UsageEntry(date: Date(), snapshot: snapshot))
        }
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<UsageEntry>) -> Void) {
        loadBuoySnapshot { snapshot in
            let now = Date()
            let nextRefresh = Calendar.current.date(byAdding: .minute, value: 5, to: now) ?? now.addingTimeInterval(300)
            completion(Timeline(entries: [UsageEntry(date: now, snapshot: snapshot)], policy: .after(nextRefresh)))
        }
    }

}

private let snapshotFileURL = URL(fileURLWithPath: "/Users/\(NSUserName())/Library/Application Support/Buoy/widget-snapshot.json")

/// 从主程序在本机起的只读接口取数；主程序没开时退回上次落盘的快照。
private func loadBuoySnapshot(completion: @escaping (UsageSnapshot) -> Void) {
    var request = URLRequest(url: snapshotURL)
    request.timeoutInterval = 2
    request.cachePolicy = .reloadIgnoringLocalCacheData
    URLSession.shared.dataTask(with: request) { data, _, _ in
        let live = data.flatMap { try? JSONDecoder().decode(UsageSnapshot.self, from: $0) }
        let snapshot = live ?? loadCachedBuoySnapshot()
        DispatchQueue.main.async { completion(snapshot) }
    }.resume()
}

private func loadCachedBuoySnapshot() -> UsageSnapshot {
    guard let data = try? Data(contentsOf: snapshotFileURL),
          let snapshot = try? JSONDecoder().decode(UsageSnapshot.self, from: data) else {
        return .empty
    }
    return snapshot
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
        .padding(.leading, 18)
        .padding(.top, 14)
        .padding(.trailing, 14)
        .padding(.bottom, 12)
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
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .bottom, spacing: 14) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(valueText)
                        .font(.system(size: 32, weight: .semibold, design: .rounded))
                        .monospacedDigit()
                        .minimumScaleFactor(0.6)
                        .lineLimit(1)
                    Text(captionText)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }

                Spacer(minLength: 6)

                HStack(alignment: .bottom, spacing: 18) {
                    metric("请求", formatCount(entry.snapshot.requestCount))
                    metric("RPM", formatRate(entry.snapshot.averageRpm))
                    metric("TPM", formatRate(entry.snapshot.averageTpm))
                }
                .fixedSize(horizontal: true, vertical: false)
                .layoutPriority(1)
            }

            Spacer(minLength: 8)

            if entry.snapshot.isConfigured {
                Divider().opacity(0.35)
                VStack(spacing: 3) {
                    if entry.snapshot.topModels.isEmpty {
                        Text("暂无模型数据")
                            .font(.system(size: 10))
                            .foregroundStyle(.secondary)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    } else {
                        ForEach(Array(entry.snapshot.topModels.prefix(3).enumerated()), id: \.offset) { index, model in
                            modelRow(rank: index + 1, model: model)
                        }
                    }
                }
                .padding(.top, 7)

                if entry.snapshot.topModels.count < 3 {
                    Spacer(minLength: 4)
                    Text(modelSummaryText)
                        .font(.system(size: 9))
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    private var modelSummaryText: String {
        let count = entry.snapshot.topModels.count
        let models = count == 0 ? "暂无模型" : "共 \(count) 个模型"
        return "\(models) · \(entry.snapshot.rangeText)消耗 \(formatCost(entry.snapshot.costUsd))"
    }

    private func modelRow(rank: Int, model: UsageModel) -> some View {
        HStack(spacing: 6) {
            Text("\(rank)")
                .font(.system(size: 9, weight: .semibold))
                .foregroundStyle(.secondary)
                .frame(width: 8, alignment: .leading)
            Text(model.model)
                .font(.system(size: 10.5, weight: .medium))
                .lineLimit(1)
                .truncationMode(.middle)
            Spacer(minLength: 6)
            Text(modelDetail(model))
                .font(.system(size: 9))
                .foregroundStyle(.secondary)
                .monospacedDigit()
                .lineLimit(1)
        }
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
        return "\(entry.snapshot.rangeText) TOKEN"
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
}

struct QuotaProvider: TimelineProvider {
    func placeholder(in context: Context) -> QuotaEntry {
        QuotaEntry(date: Date(), snapshot: .quotaPreview)
    }

    func getSnapshot(in context: Context, completion: @escaping (QuotaEntry) -> Void) {
        loadBuoySnapshot { completion(QuotaEntry(date: Date(), snapshot: $0)) }
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<QuotaEntry>) -> Void) {
        loadBuoySnapshot { snapshot in
            let now = Date()
            let nextRefresh = Calendar.current.date(byAdding: .minute, value: 5, to: now) ?? now.addingTimeInterval(300)
            completion(Timeline(entries: [QuotaEntry(date: now, snapshot: snapshot)], policy: .after(nextRefresh)))
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
            updatedAtUnixSeconds: Int64(Date().timeIntervalSince1970),
            models: [
                QuotaModelSnapshot(model: "gemini-3-pro", label: "Gemini 3 Pro", remainingFraction: 0.72),
                QuotaModelSnapshot(model: "claude-sonnet-4-5", label: "Claude Sonnet 4.5", remainingFraction: 0.41),
                QuotaModelSnapshot(model: "gpt-5", label: "GPT-5", remainingFraction: 0.18)
            ]))
}

struct QuotaGroupSnapshot: Identifiable {
    let title: String
    let fraction: Double

    var id: String { title }
}

struct RingMeter: View {
    let fraction: Double
    var lineWidth: CGFloat = 3.5

    private var color: Color {
        if fraction > 0.5 { return .green }
        if fraction > 0.2 { return .orange }
        return .red
    }

    var body: some View {
        ZStack {
            Circle()
                .stroke(color.opacity(0.14), lineWidth: lineWidth)
            Circle()
                .trim(from: 0, to: max(0.02, min(1, fraction)))
                .stroke(color, style: StrokeStyle(lineWidth: lineWidth, lineCap: .round))
                .rotationEffect(.degrees(-90))
        }
    }
}

struct QuotaWidgetView: View {
    let entry: QuotaEntry
    @Environment(\.widgetFamily) private var family

    private var quota: QuotaSnapshot? { entry.snapshot.quota }

    private var groups: [QuotaGroupSnapshot] {
        let models = quota?.models ?? []
        let gemini = models.filter { $0.model.lowercased().hasPrefix("gemini") }
        let others = models.filter {
            let name = $0.model.lowercased()
            return name.hasPrefix("claude") || name.hasPrefix("gpt")
        }

        var result: [QuotaGroupSnapshot] = []
        if let fraction = worstFraction(gemini) {
            result.append(QuotaGroupSnapshot(title: "Gemini 模型", fraction: fraction))
        }
        if let fraction = worstFraction(others) {
            result.append(QuotaGroupSnapshot(title: "Claude 和 GPT 模型", fraction: fraction))
        }
        if result.isEmpty, let fraction = worstFraction(models) {
            result.append(QuotaGroupSnapshot(title: "剩余额度", fraction: fraction))
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
        .padding(.leading, 18)
        .padding(.top, 14)
        .padding(.trailing, 14)
        .padding(.bottom, 12)
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

            Spacer(minLength: 4)

            if groups.isEmpty {
                Text(statusText)
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
                    .lineLimit(3)
                Spacer(minLength: 0)
            } else {
                HStack(spacing: 6) {
                    ForEach(Array(groups.prefix(2))) { group in
                        VStack(spacing: 5) {
                            RingMeter(fraction: group.fraction, lineWidth: 5)
                                .frame(width: 46, height: 46)
                            Text(percentText(group.fraction))
                                .font(.system(size: 13, weight: .semibold, design: .rounded))
                                .monospacedDigit()
                            Text(group.title)
                                .font(.system(size: 8))
                                .foregroundStyle(.secondary)
                                .lineLimit(1)
                                .minimumScaleFactor(0.7)
                        }
                        .frame(maxWidth: .infinity)
                    }
                }
                Spacer(minLength: 0)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    private var medium: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text("Antigravity 配额")
                    .font(.system(size: 11, weight: .semibold))
                Spacer(minLength: 4)
                Text(creditsText)
                    .font(.system(size: 9))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }

            Spacer(minLength: 6)

            if groups.isEmpty {
                Text(statusText)
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
                Spacer(minLength: 0)
            } else {
                VStack(spacing: 7) {
                    ForEach(groups) { group in
                        groupRow(group)
                    }
                }
                Spacer(minLength: 0)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    private func groupRow(_ group: QuotaGroupSnapshot) -> some View {
        HStack(spacing: 10) {
            VStack(alignment: .leading, spacing: 2) {
                Text(group.title)
                    .font(.system(size: 10.5, weight: .medium))
                    .lineLimit(1)
                Text("剩余额度")
                    .font(.system(size: 9))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer(minLength: 6)
            Text(percentText(group.fraction))
                .font(.system(size: 16, weight: .semibold, design: .rounded))
                .monospacedDigit()
            RingMeter(fraction: group.fraction)
                .frame(width: 26, height: 26)
        }
        .padding(.horizontal, 9)
        .padding(.vertical, 6)
        .background(
            RoundedRectangle(cornerRadius: 9, style: .continuous)
                .fill(Color.primary.opacity(0.05))
        )
    }

    private func worstFraction(_ models: [QuotaModelSnapshot]) -> Double? {
        models.compactMap { $0.remainingFraction }.min()
    }

    private func clamp(_ value: Double) -> Double {
        max(0, min(1, value))
    }

    private func percentText(_ fraction: Double) -> String {
        "\(Int((clamp(fraction) * 100).rounded()))%"
    }

    private var creditsText: String {
        guard let quota else { return "未同步" }
        if let available = quota.availableCredits {
            if let monthly = quota.monthlyCredits, monthly > 0 {
                return "积分 \(Int(available.rounded())) / \(Int(monthly.rounded()))"
            }
            return "积分 \(Int(available.rounded()))"
        }
        return quota.planName ?? ""
    }

    private var statusText: String {
        guard let quota else { return "在 Buoy 设置里启用本地 Antigravity" }
        if quota.models.isEmpty {
            return quota.message.isEmpty ? "还没有读到配额" : quota.message
        }
        return quota.message
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
    }
}
