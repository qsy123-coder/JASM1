using GIMI_ModManager.WinUI.Contracts.Services;
using GIMI_ModManager.WinUI.Models;
using GIMI_ModManager.WinUI.ViewModels;
using GIMI_ModManager.WinUI.Views.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace GIMI_ModManager.WinUI.Views;

/// <summary>
/// 「每日更新」页 —— 最近 7 天有更新的 mod，按本地日期分节。
///
/// <para>
/// 卡片这一套（<see cref="CardScaler"/> / 懒加载 / 失败重试 / 图片事件）与 <c>ModMarketPage</c>
/// **刻意重复**：那段逻辑已经实机跑过、形状稳定，为一个新页面去抽公共件要动到市场页，
/// 属于另一次独立改动（两处注释里都标了这笔账）。改这里的卡片行为时，务必同步回 ModMarketPage。
/// </para>
///
/// <para>
/// 与市场页的结构差异只有一处：市场是**一个**卡片面板、增量追加；这里是**每个日期一个**面板、
/// 每次加载整体重建（7 天窗口一次拉完，没有"加载更多"）。
/// </para>
/// </summary>
public sealed partial class DailyUpdatesPage : Page
{
    public DailyUpdatesViewModel ViewModel { get; }

    // 懒加载:已加卡但尚未设图片源的卡片。滚到视口附近才设置 Source(COS 拉图),
    // 离屏卡片的进度圈默认折叠、不空转。
    private readonly List<PendingCard> _pendingImages = [];

    /// <summary>已设过 Source、但还没等到 ImageOpened/ImageFailed 的图 —— 失败时靠它找回对应的卡片。</summary>
    private readonly Dictionary<Image, PendingCard> _claimedImages = [];

    /// <summary>失败重试用的重复计时器，见 <see cref="EnsureRetryTimer"/>。</summary>
    private DispatcherQueueTimer? _retryTimer;

    /// <summary>各日期节在可视树里的落点。日期导航要靠它做滚动定位与高亮判定。</summary>
    private readonly List<DaySection> _sections = [];

    /// <summary>
    /// 滚动高亮的判定线（px）。**照抄站点的 scroll-spy**:那边用 <c>probe = 150</c> ——
    /// 「一段的顶边越过视口顶部 150px 就算当前段」。太小会让刚露头的段抢走高亮，太大则慢半拍。
    /// </summary>
    private const double NavProbePixels = 150;

    /// <summary>
    /// true = 这次改 <c>SelectedItem</c> 是滚动高亮干的，不是用户点的。
    ///
    /// <para>
    /// ListView 的 SelectionChanged 分不出这两种来源。不标这一下，滚动每帧设一次选中
    /// 就会被当成一次点击、反过来触发滚动，两个方向互相打架（表现为滚起来一顿一顿）。
    /// </para>
    /// </summary>
    private bool _syncingNavSelection;

    public DailyUpdatesPage()
    {
        ViewModel = App.GetService<DailyUpdatesViewModel>();
        InitializeComponent();

        // 首次进入时 ScrollViewer 还没被量过，ViewportHeight 为 0 —— 那样 OnGroupsRebuilt 里那两次
        // LazyLoadVisibleImages() 都会提前返回，而之后又没有第三个触发点，首屏那批卡片的图就永远
        // 没人认领。等它拿到真实尺寸时补一次。
        CardScrollViewer.SizeChanged += (_, _) => LazyLoadVisibleImages();
        ViewModel.GroupsRebuilt += (_, _) => OnGroupsRebuilt();
        ViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(DailyUpdatesViewModel.SelectedMod))
                OnSelectedModChanged();
        };
        // 关闭请求经 ViewModel 重置 SelectedMod=null,保证下次点同一 mod 时 PropertyChanged 能触发
        DetailPanel.Closed += (_, _) => ViewModel.CloseDetailPanelCommand.Execute(null);
    }

    private void OnSelectedModChanged()
    {
        if (ViewModel.SelectedMod is { } mod)
            DetailPanel.Show(mod);
        else
            DetailPanel.Hide();
    }

    /// <summary>
    /// 返回 Mod 市场。走 Frame 的返回栈，不硬编码目标页 —— 这一页是怎么进来的（市场工具栏按钮）
    /// 是调用方的事，这里只负责退一步。栈空时不做事（理论上进不来，进来也不会崩）。
    /// </summary>
    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        var navigationService = App.GetService<INavigationService>();
        if (navigationService.CanGoBack)
            navigationService.GoBack();
    }

    /// <summary>
    /// 整棵分节树重建。加载完成后 <see cref="DailyUpdatesViewModel.Groups"/> 是整体替换的，
    /// 所以这里清空重来 —— 不做增量合并，增量路径反而要处理"哪一组多了哪一条"。
    /// </summary>
    private void OnGroupsRebuilt()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _pendingImages.Clear();
            // 旧卡片已被丢弃，别再替它们记着「认领待确认」—— 否则迟到的失败回调会把
            // 已经不在树上的卡塞回待认领队列（那样每次认领都会在 TransformToVisual 上抛）。
            _claimedImages.Clear();
            GroupsPanel.Children.Clear();
            _sections.Clear();

            // 导航只列**有更新的天**（与站点的 daysWithMods 一致）—— 一列全是"没更新"的条目
            // 只会把真正有东西的那几天挤没。空天仍然留在正文里（「今天还没更新」本身是信息）。
            var navDays = new List<DailyUpdateGroup>();

            foreach (var group in ViewModel.Groups)
            {
                // 一节一个容器：标题 + （卡片面 或 空天说明）。容器本身就是导航的滚动目标，
                // 所以下面记的是它、不是卡片面板 —— 滚过去要连标题一起看见。
                var section = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
                section.Children.Add(BuildGroupHeader(group));

                if (group.IsEmpty)
                {
                    // 空天只出一行说明，不铺一个空面板 —— 「今天还没更新」是要让人看见的，
                    // 但不该在页面上占一块和真实卡片一样高的空白。
                    section.Children.Add(BuildEmptyDayNote());
                }
                else
                {
                    navDays.Add(group);

                    var panel = new WrapGridPanel
                    {
                        // 与市场同为「固定列数 + 窄了自动降列」，但列数比市场多两列（8 对 6）：
                        // 这一页是"扫量"的地方，一屏能站几排比单张卡片够不够大重要。
                        // 图片区也已经从 9:16 压到 4:5（见 XAML），两处合起来才是"每屏更多"。
                        Columns = 8,
                        MinItemWidth = 180,
                        VerticalAlignment = VerticalAlignment.Top
                    };
                    foreach (var mod in group.Mods)
                        AddCard(panel, mod);

                    section.Children.Add(panel);
                }

                _sections.Add(new DaySection(group, section));
                GroupsPanel.Children.Add(section);
            }

            DayNavListView.ItemsSource = navDays;

            // Force layout so ScrollViewer picks up new extent
            GroupsPanel.UpdateLayout();

            // 布局定了才有正确的 Y，这时才谈得上判高亮。
            UpdateActiveDay();

            // 懒加载:先加载当前视口内的图;再排一帧兜底(首次布局完成后 ViewportHeight 才可读)
            LazyLoadVisibleImages();
            DispatcherQueue.TryEnqueue(() => LazyLoadVisibleImages());
        });
    }

    // ─── 日期导航 ──────────────────────────────────────────────

    /// <summary>用户点了某一天 —— 滚到那一段。滚动高亮自己改的选中不算（见 <see cref="_syncingNavSelection"/>）。</summary>
    private void DayNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingNavSelection) return;

        if (DayNavListView.SelectedItem is DailyUpdateGroup group)
            ScrollToDate(group.Date);
    }

    /// <summary>点导航：把那一段滚到视口顶部。与站点 <c>scrollIntoView({block:"start"})</c> 同义。</summary>
    private void ScrollToDate(DateOnly date)
    {
        var section = _sections.FirstOrDefault(s => s.Group.Date == date);
        if (section is null) return;

        if (TryGetSectionOffset(section, out var y) && !double.IsNaN(y))
            CardScrollViewer.ChangeView(null, y, null);
    }

    /// <summary>
    /// 滚动监听：高亮「当前视口最靠顶的那一天」。站点用 rAF 节流，这里没那个必要 ——
    /// 一节一次 TransformToVisual，最多 7 次，比一帧的布局便宜得多。
    /// </summary>
    private void UpdateActiveDay()
    {
        if (_sections.Count == 0) return;

        var probe = CardScrollViewer.VerticalOffset + NavProbePixels;

        DailyUpdateGroup? current = null;
        foreach (var section in _sections)
        {
            if (section.Group.IsEmpty) continue; // 空天不进导航，也就不能当高亮项
            if (!TryGetSectionOffset(section, out var y)) continue;
            if (y <= probe) current = section.Group;
            else break;
        }

        // 一条都还没过线时（刚进页面、或者页面顶部那几段是空天）让最上面有内容的那条亮着，
        // 免得出「导航整列全灭」这种看着像坏了的状态。
        current ??= _sections.FirstOrDefault(s => !s.Group.IsEmpty)?.Group;
        if (current is null) return;

        // 选中项没变就别动它：每帧重设一次会让 ListView 自己反复 ScrollIntoView，
        // 用户手动滚动时会被它往回拽。
        if (ReferenceEquals(DayNavListView.SelectedItem, current)) return;

        _syncingNavSelection = true;
        try
        {
            DayNavListView.SelectedItem = current;
        }
        finally
        {
            _syncingNavSelection = false;
        }
    }

    /// <summary>
    /// 一段相对**滚动内容**顶部的偏移。坐标系是 <c>GroupsPanel</c>（ScrollViewer 的内容根），
    /// 所以这个值可以直接和 <c>VerticalOffset</c> 比、也可以直接喂给 <c>ChangeView</c>。
    /// 取不到（还没进可视树）时返回 false，调用方跳过它。
    /// </summary>
    private bool TryGetSectionOffset(DaySection section, out double offset)
    {
        try
        {
            offset = section.Root.TransformToVisual(GroupsPanel).TransformPoint(new Point(0, 0)).Y;
            return true;
        }
        catch
        {
            offset = 0;
            return false;
        }
    }


    /// <summary>
    /// 日期节的标题，如「2026年10月14日（今天）  (12)」。
    /// 用代码建而不是再写一个 DataTemplate：它就是一个 TextBlock，套模板只是多一层间接。
    /// </summary>
    private static TextBlock BuildGroupHeader(DailyUpdateGroup group) => new()
    {
        // 空天不挂「(0)」—— 那只是把"没有"说两遍，下面那行说明已经说清了。
        Text = group.IsEmpty ? group.Title : $"{group.Title}  ({group.Mods.Count})",
        FontSize = 18,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(20, 20, 20, 0)
    };

    /// <summary>空天的那行说明。颜色与市场卡片里的次要文字同款(#8C8C8D)，不抢戏。</summary>
    private static TextBlock BuildEmptyDayNote() => new()
    {
        Text = "这天没有新增",
        FontSize = 13,
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x8C, 0x8C, 0x8D)),
        Margin = new Thickness(20, 6, 20, 0)
    };

    private void AddCard(WrapGridPanel panel, ModMarketMod mod)
    {
        var tpl = Resources["ModCardTemplate"] as DataTemplate;
        if (tpl is null) return;
        var card = tpl.LoadContent() as FrameworkElement;
        if (card is null) return;
        card.DataContext = mod;
        // Wire click to show detail panel
        card.Tapped += (s, e) =>
        {
            ViewModel.OpenModDetailCommand.Execute(mod);
        };
        card.IsTapEnabled = true;

        // 模板里的尺寸是照参考图写的设计值,而卡片宽度是容器均分来的、会随窗口变 ——
        // 整卡按槽位宽等比缩放,比例才在任何窗口宽度下都与市场一致。
        if (CardScaler.Collect(card) is { } scaler)
            card.SizeChanged += (_, e) => scaler.Apply(e.NewSize.Width);

        // 卡片进可视树的那一刻再补一次：AddCard 时它还不在树里，TransformToVisual 会抛，
        // 那次认领只能 continue 掉。
        card.Loaded += (_, _) => LazyLoadVisibleImages();

        // 懒加载:无预览图直接隐藏加载圈;有图的挂到待加载队列,滚动到视口才设 Source
        var thumb = FindByName<Image>(card, "CardImage");
        if (mod.PreviewImageUrl is null)
        {
            // 没有预览图的卡片永不触发 ImageOpened/ImageFailed，需要直接隐藏加载圈，避免转圈不停
            if (FindByName<ProgressRing>(card, "CardLoadingRing") is { } idleRing)
            {
                idleRing.IsActive = false;
                idleRing.Visibility = Visibility.Collapsed;
            }
        }
        else if (thumb is not null)
        {
            _pendingImages.Add(new PendingCard
            {
                Card = card,
                Thumb = thumb,
                Backdrop = FindByName<Image>(card, "CardImageBackdrop"),
                // 按名字在**逻辑树**里找(此刻卡片还没进可视树,VisualTreeHelper 认不到)
                Ring = FindByName<ProgressRing>(card, "CardLoadingRing"),
                Url = mod.PreviewImageUrl! // 上面已判过非空
            });
        }
        panel.Children.Add(card);
    }

    /// <summary>设计稿的列距 = 卡片外框 304 + 左右间距 26。槽位宽除以它就是缩放系数 s。</summary>
    private const double DesignPitchWidth = 330;

    /// <summary>写回尺寸时差值低于这个数就当没变 —— 免得自己再触发一场布局,来回没完。</summary>
    private const double MetricEpsilon = 0.01;

    /// <summary>
    /// 把一张卡片的「设计值」整体乘上缩放系数 s 写回去。
    ///
    /// <para>
    /// 模板里的尺寸是照参考图逐像素量出来的设计值(卡片外框 304 宽那一套),而卡片实际宽度是
    /// <c>WrapGridPanel</c> 均分槽位得来的、会随窗口变,所以字号 / 内边距 / 外边距 / 行高 /
    /// 圆角要**一起**缩放 —— 只缩封面高度的话,卡片比例会随窗口漂移。
    /// </para>
    ///
    /// <para>
    /// 这份实现与 <c>ModMarketPage.CardScaler</c> / <c>ModStorePage.CardScaler</c> **刻意重复**
    /// (照该仓库既有的做法:几份都注释清楚、等抽公共件那次改动一起收)。改一处记得改另一处 ——
    /// 三个页面的卡片版式必须完全一致,否则「市场严格用商店的布局」这条约定就断了。
    /// </para>
    /// </summary>
    private sealed class CardScaler
    {
        private readonly Border _root;
        private readonly List<Metric> _metrics;

        /// <summary>
        /// 上一次写进去的左右外边距之和。
        ///
        /// 缩放系数要用「槽位宽」算,而 <c>SizeChanged</c> 给的宽度是 <c>ActualWidth</c> —— **不含 Margin**;
        /// 卡片外边距本身又在缩放集合里,拿实宽直接算就会和「写回后实宽又变了」互相追。
        /// 用上一次写进去的值把槽位宽反推回来(槽位宽 = 实宽 + 左右外边距),等式一次就成立,不再迭代。
        /// </summary>
        private double _appliedMarginH;

        private CardScaler(Border root, List<Metric> metrics)
        {
            _root = root;
            _metrics = metrics;
            _appliedMarginH = root.Margin.Left + root.Margin.Right; // 首帧还没写过,就是 XAML 里的设计值
        }

        /// <summary>收集一张卡片的设计值。卡片根不是 Border(模板被改过)时返回 null,调用方跳过。</summary>
        public static CardScaler? Collect(FrameworkElement card)
        {
            if (card is not Border root) return null;

            var metrics = new List<Metric>();
            Walk(card, metrics);
            return new CardScaler(root, metrics);
        }

        /// <summary>按卡片实宽重写整张卡。</summary>
        public void Apply(double cardWidth)
        {
            var scale = (cardWidth + _appliedMarginH) / DesignPitchWidth;
            foreach (var metric in _metrics)
                metric.Apply(scale);

            _appliedMarginH = _root.Margin.Left + _root.Margin.Right;
        }

        /// <summary>
        /// 沿**逻辑树**走一遍卡片,把要跟着缩放的尺寸登记下来。
        ///
        /// 只收「尺寸类」属性(字号 / 间距 / 内外边距 / 宽高 / 圆角 / 定高行);边框粗细、字重、
        /// MaxLines、颜色一律不缩放 —— 边框要一直是发丝级的 1px,行数上限也不是尺寸。
        /// </summary>
        private static void Walk(DependencyObject node, List<Metric> metrics)
        {
            if (node is FrameworkElement element)
            {
                Track(metrics, () => element.Width, v => element.Width = v);
                Track(metrics, () => element.Height, v => element.Height = v);
                Track(metrics, () => element.MinWidth, v => element.MinWidth = v);
                Track(metrics, () => element.MinHeight, v => element.MinHeight = v);
                TrackThickness(metrics, () => element.Margin, v => element.Margin = v);
            }

            switch (node)
            {
                case TextBlock text:
                    Track(metrics, () => text.FontSize, v => text.FontSize = v);
                    // 写死的行高也要跟着缩:不然字号缩了、行高还留在设计值上,两行就撑破了预定高度。
                    Track(metrics, () => text.LineHeight, v => text.LineHeight = v);
                    break;
                case FontIcon icon:
                    Track(metrics, () => icon.FontSize, v => icon.FontSize = v);
                    break;
                case StackPanel panel:
                    Track(metrics, () => panel.Spacing, v => panel.Spacing = v);
                    TrackThickness(metrics, () => panel.Padding, v => panel.Padding = v);
                    break;
                case Grid grid:
                    Track(metrics, () => grid.RowSpacing, v => grid.RowSpacing = v);
                    Track(metrics, () => grid.ColumnSpacing, v => grid.ColumnSpacing = v);
                    TrackThickness(metrics, () => grid.Padding, v => grid.Padding = v);
                    // 定高行也要跟着缩 —— RowDefinition 不是 FrameworkElement,遍历认不到它,得单独登记
                    foreach (var row in grid.RowDefinitions)
                        TrackRowHeight(metrics, row);
                    break;
                case Border border:
                    TrackThickness(metrics, () => border.Padding, v => border.Padding = v);
                    TrackCornerRadius(metrics, border);
                    break;
            }

            foreach (var child in LogicalChildren(node))
                Walk(child, metrics);
        }

        /// <summary>登记一个标量设计值(字号 / 间距 / 宽高)。没显式设过(NaN)或非正的不登记。</summary>
        private static void Track(List<Metric> metrics, Func<double> read, Action<double> write)
        {
            var design = read();
            if (double.IsNaN(design) || design <= 0) return;

            metrics.Add(new Metric(s =>
            {
                var value = design * s;
                if (Math.Abs(read() - value) > MetricEpsilon) write(value);
            }));
        }

        /// <summary>登记一个 Thickness(内 / 外边距):四个分量一起乘 s。</summary>
        private static void TrackThickness(List<Metric> metrics, Func<Thickness> read, Action<Thickness> write)
        {
            var design = read();
            if (design is { Left: 0, Top: 0, Right: 0, Bottom: 0 }) return;

            metrics.Add(new Metric(s =>
            {
                var scaled = new Thickness(design.Left * s, design.Top * s, design.Right * s, design.Bottom * s);
                if (Math.Abs(read().Left - scaled.Left) > MetricEpsilon) write(scaled);
            }));
        }

        /// <summary>登记一个 Grid 的定高行。Auto / Star 行不登记(它们本来就该由内容或剩余空间定)。</summary>
        private static void TrackRowHeight(List<Metric> metrics, RowDefinition row)
        {
            var design = row.Height;
            if (!design.IsAbsolute || design.Value <= 0) return;

            metrics.Add(new Metric(s =>
            {
                var value = design.Value * s;
                if (Math.Abs(row.Height.Value - value) > MetricEpsilon) row.Height = new GridLength(value);
            }));
        }

        /// <summary>登记一个 CornerRadius:四个角一起乘 s。</summary>
        private static void TrackCornerRadius(List<Metric> metrics, Border border)
        {
            var design = border.CornerRadius;
            if (design is { TopLeft: 0, TopRight: 0, BottomRight: 0, BottomLeft: 0 }) return;

            metrics.Add(new Metric(s =>
            {
                var scaled = new CornerRadius(
                    design.TopLeft * s, design.TopRight * s, design.BottomRight * s, design.BottomLeft * s);
                if (Math.Abs(border.CornerRadius.TopLeft - scaled.TopLeft) > MetricEpsilon)
                    border.CornerRadius = scaled;
            }));
        }
    }

    /// <summary>一条登记项:一个设计值 + 一个「按缩放系数 s 重写自己」的动作(动作自带差值守卫)。</summary>
    private sealed class Metric(Action<double> applyByScale)
    {
        public void Apply(double scale) => applyByScale(scale);
    }

    /// <summary>
    /// 逻辑树里的直接子元素 —— 只认 <c>Panel.Children</c> / <c>Border.Child</c> /
    /// <c>ContentControl.Content</c> 三类容器,卡片模板用到的就是这三类。
    ///
    /// 为什么不走 <c>VisualTreeHelper</c>:卡片是 <c>LoadContent()</c> 刚实例化出来的,还没进可视树,
    /// 那一刻可视化树是空的。
    /// </summary>
    private static IEnumerable<DependencyObject> LogicalChildren(DependencyObject node)
    {
        switch (node)
        {
            case Panel panel:
                foreach (var child in panel.Children)
                    yield return child;
                break;
            case Border { Child: { } borderChild }:
                yield return borderChild;
                break;
            case ContentControl { Content: DependencyObject content }:
                yield return content;
                break;
        }
    }

    /// <summary>在**逻辑树**里按名字找模板里的元素(那些 x:Name),理由见 <see cref="LogicalChildren"/>。</summary>
    private static T? FindByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T hit && hit.Name == name)
            return hit;

        foreach (var child in LogicalChildren(root))
            if (FindByName<T>(child, name) is { } found)
                return found;

        return null;
    }

    /// <summary>把滚动到视口(含少量预加载缓冲)内的卡片图片源设置好，并从待加载队列移除。</summary>
    private void LazyLoadVisibleImages()
    {
        if (_pendingImages.Count == 0) return;
        var h = CardScrollViewer.ViewportHeight;
        if (h <= 0) return; // 尚未完成布局,下一次再触发
        // 预加载缓冲:视口上下各 0.5 屏,滚到时已就绪,避免白板闪烁
        var overscan = h * 0.5;
        var top = -overscan;
        var bottom = h + overscan;
        for (var i = _pendingImages.Count - 1; i >= 0; i--)
        {
            var e = _pendingImages[i];
            // 上一次失败之后还在退避窗口里，这一轮先放过它
            if (e.NextRetryAt > DateTimeOffset.Now) continue;
            double y;
            try
            {
                y = e.Card.TransformToVisual(CardScrollViewer).TransformPoint(new Point(0, 0)).Y;
            }
            catch
            {
                continue;
            }
            if (y < top || y > bottom) continue;
            if (e.Ring is not null)
            {
                e.Ring.Visibility = Visibility.Visible;
                e.Ring.IsActive = true;
            }
            if (e.Thumb is { } thumb)
            {
                e.Attempts++;
                // 第 2 次起绕开 XAML 那个按 URI 的图像缓存:失败过一次的 URI 可能已在缓存里留了痕,
                // 不绕的话重试会直接吃到上次那份失败结果。
                thumb.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(e.Url))
                {
                    CreateOptions = e.Attempts > 1
                        ? Microsoft.UI.Xaml.Media.Imaging.BitmapCreateOptions.IgnoreImageCache
                        : Microsoft.UI.Xaml.Media.Imaging.BitmapCreateOptions.None
                };
                _claimedImages[thumb] = e;
            }

            // 模糊背景:同一张图、极低分辨率(拉到卡片宽就是一片柔和色块),填住主图两边的窄边
            if (e.Backdrop is not null)
                e.Backdrop.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(e.Url))
                {
                    DecodePixelWidth = BackdropDecodeWidth
                };

            _pendingImages.RemoveAt(i);
        }
    }

    /// <summary>
    /// 失败重试的退避起点。懒加载只认领一次，没有重试的话一次网络抖动就是一张永久空卡；
    /// 也**不设「试几次就放弃」**——放弃等于又回到「一次网络抖动就是一张永久空卡」。
    /// 退避本身已经把死链的代价压到「只要还留在视口里就每分钟一次请求」，可以一直等网络恢复。
    /// </summary>
    private static readonly TimeSpan RetryBackoffBase = TimeSpan.FromSeconds(3);

    /// <summary>退避上限：翻倍到这儿就停住，死链每分钟最多试一次。</summary>
    private static readonly TimeSpan RetryBackoffCap = TimeSpan.FromSeconds(60);

    /// <summary>重试计时器的轮询间隔 —— 够密，才不会被某张卡的退避窗口整段错过去。</summary>
    private static readonly TimeSpan RetryPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>第 <paramref name="attempts"/> 次失败之后的等待时长：起点 3s、每失败一次翻倍、封顶 60s。</summary>
    private static TimeSpan BackoffFor(int attempts)
    {
        var seconds = RetryBackoffBase.TotalSeconds * Math.Pow(2, attempts - 1);
        return TimeSpan.FromSeconds(Math.Min(seconds, RetryBackoffCap.TotalSeconds));
    }

    /// <summary>
    /// 一个日期节在可视树里的落点。导航的滚动定位与高亮判定都要靠它。
    /// </summary>
    /// <param name="Root">整节的容器（标题 + 卡片面板），取偏移量用的是它的 Y。</param>
    private sealed record DaySection(DailyUpdateGroup Group, FrameworkElement Root);

    /// <summary>待懒加载的卡片状态。</summary>
    private sealed class PendingCard
    {
        public FrameworkElement Card = null!;
        public Image? Thumb;

        /// <summary>已经设过几次 Source（含首次）。用来算下一次重试要退避多久。</summary>
        public int Attempts;

        /// <summary>下一次允许重试的时刻（退避窗口的右端）。默认值 = 随时可以认领。</summary>
        public DateTimeOffset NextRetryAt;

        /// <summary>非空 —— 队列里只会放「有预览图」的卡片（见 <see cref="AddCard"/>）。</summary>
        public string Url = null!;

        /// <summary>主图后面那层模糊背景(同一张图、极低分辨率),用来填住 Uniform 留下的窄边。</summary>
        public Image? Backdrop;

        public ProgressRing? Ring;
    }

    /// <summary>
    /// 模糊背景的解码宽度。
    ///
    /// 市场里的预览图实测中位数是 750×1268 那种竖屏(宽高比 0.56),卡片图片区宽 304 ——
    /// 取 16 是约 19 倍放大:糊到只剩色块与明暗走向(实机反馈「还是不够模糊」,32 那档还看得出细节)。
    /// 想更糊就再往下调(8 就基本是纯色块了),想看清一点就往上调。
    /// </summary>
    private const int BackdropDecodeWidth = 16;

    /// <summary>图片加载成功：隐藏卡片加载圈，并把它从「认领待确认」里摘掉（成功了就不会再重试）</summary>
    private void CardImage_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image) return;
        _claimedImages.Remove(image);
        if (image.Parent is Grid grid) HideCardLoadingRing(grid);
    }

    /// <summary>图片加载失败：隐藏卡片加载圈（占位底色仍然可见），并排队重试</summary>
    private void CardImage_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is not Image image) return;
        if (image.Parent is Grid grid) HideCardLoadingRing(grid);
        QueueImageRetry(image);
    }

    /// <summary>
    /// 这次没加载成功，把卡片放回待认领队列。
    ///
    /// <para>
    /// 懒加载只认领一次、认领即出队 —— 若那一次恰好失败（刚开机、代理还在握手、网络正好抖一下），
    /// 这张卡就再没人管，永久停在背景色上，非整页重建不可。实机现象「首屏十几张一直空着，
    /// 之后滚出来的都正常」就是这个：它们是在网络还没就绪时被认领的，而队列里已经没有它们了。
    /// </para>
    /// </summary>
    private void QueueImageRetry(Image image)
    {
        // 不在这张表里 = 不是我们认领的（或已经成功过），不该管
        if (!_claimedImages.Remove(image, out var pending)) return;

        // 失败一次就往后推一档：3s、6s、12s……封顶 60s。不设「试几次就放弃」，
        // 网络恢复后不管隔了多久，只要卡片还在视口里就一定能自己好。
        pending.NextRetryAt = DateTimeOffset.Now + BackoffFor(pending.Attempts);
        _pendingImages.Add(pending);
        EnsureRetryTimer();
    }

    /// <summary>
    /// 保证重试计时器在跑。用计时器而不是只靠滚动，是为了让首屏在用户不动手的情况下也能自愈；
    /// 跑的还是 <see cref="LazyLoadVisibleImages"/>，所以只有仍落在视口内、且退避窗口已过的卡会被重试。
    ///
    /// <para>它是**重复**计时器（而不是每次失败重排一个单次计时器）：各张卡的退避时长不同，
    /// 单次计时器会被后一次的失败覆盖掉，先到期的那些就永远等不到自己的那一拍。
    /// 队列里没有卡还在等退避时就停掉，不空转。</para>
    /// </summary>
    private void EnsureRetryTimer()
    {
        if (_retryTimer is null)
        {
            _retryTimer = DispatcherQueue.CreateTimer();
            _retryTimer.Interval = RetryPollInterval;
            _retryTimer.IsRepeating = true;
            _retryTimer.Tick += (_, _) =>
            {
                LazyLoadVisibleImages();

                // 没有卡还在退避窗口里等着，就停：剩下的要么是没进过视口的新卡（滚动会认领），
                // 要么已经认领出去了。
                if (!_pendingImages.Any(pending => pending.NextRetryAt != default))
                    _retryTimer.Stop();
            };
        }

        _retryTimer.Start();
    }

    private static void HideCardLoadingRing(DependencyObject root)
    {
        if (root is ProgressRing ring)
        {
            ring.IsActive = false;
            ring.Visibility = Visibility.Collapsed;
            return;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            HideCardLoadingRing(VisualTreeHelper.GetChild(root, i));
    }

    /// <summary>
    /// 滚动时做两件事：图片懒加载，以及刷日期导航的高亮。
    /// 这里**没有**「滚到底自动加载下一页」—— 本页 7 天窗口一次拉完，不存在下一页
    /// （这是它与 Mod 市场滚动逻辑的唯一差别）。
    /// </summary>
    private void ScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        LazyLoadVisibleImages();
        UpdateActiveDay();
    }

    /// <summary>点击 NSFW 模糊遮罩后移除遮罩，显示原图</summary>
    private void NsfwOverlay_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Border overlay)
            overlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }
}