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
        rangeLabel: nil
    )
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
            rangeLabel: "今日"
        ))
    }

    func getSnapshot(in context: Context, completion: @escaping (UsageEntry) -> Void) {
        loadSnapshot { snapshot in
            completion(UsageEntry(date: Date(), snapshot: snapshot))
        }
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<UsageEntry>) -> Void) {
        loadSnapshot { snapshot in
            let now = Date()
            let nextRefresh = Calendar.current.date(byAdding: .minute, value: 5, to: now) ?? now.addingTimeInterval(300)
            completion(Timeline(entries: [UsageEntry(date: now, snapshot: snapshot)], policy: .after(nextRefresh)))
        }
    }

    /// 从主程序在本机起的只读接口取数；主程序没开时退回空状态。
    private func loadSnapshot(completion: @escaping (UsageSnapshot) -> Void) {
        var request = URLRequest(url: snapshotURL)
        request.timeoutInterval = 2
        request.cachePolicy = .reloadIgnoringLocalCacheData
        URLSession.shared.dataTask(with: request) { data, _, _ in
            let live = data.flatMap { try? JSONDecoder().decode(UsageSnapshot.self, from: $0) }
            let snapshot = live ?? self.loadCachedSnapshot()
            DispatchQueue.main.async { completion(snapshot) }
        }.resume()
    }

    /// 主程序没开时读它上次写下的快照，至少还能看到最后一次的数据。
    private func loadCachedSnapshot() -> UsageSnapshot {
        let url = URL(fileURLWithPath: "/Users/\(NSUserName())/Library/Application Support/Buoy/widget-snapshot.json")
        guard let data = try? Data(contentsOf: url),
              let snapshot = try? JSONDecoder().decode(UsageSnapshot.self, from: data) else {
            return .empty
        }
        return snapshot
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
    }
}
