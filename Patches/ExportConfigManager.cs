using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace EncodePreviewPlugin.Patches
{
    public static class ExportConfigManager
    {
        private static object? _currentViewModel;
        private static Timeline? _currentTimeline;
        private static readonly Dictionary<IItem, bool> _hiddenBackup = new();

        public static bool IsOnlySelectedItems { get; set; } = false;
        public static bool IsFromCurrentFrame { get; set; } = false;

        public static void OnMp4ConfigOpened(object viewModel, Timeline? timeline)
        {
            _currentViewModel = viewModel;
            _currentTimeline = timeline;

            // 初期状態はOFF（安全のため毎回ダイアログ表示時はOFFで開始）
            IsOnlySelectedItems = false;
            IsFromCurrentFrame = false;
        }

        public static void UpdateEncodeRange()
        {
            if (_currentViewModel == null || _currentTimeline == null) return;

            int from = 0;
            int to = _currentTimeline.Length;

            if (IsOnlySelectedItems)
            {
                var selected = GetSelectedItems(_currentTimeline);
                if (selected.Count > 0)
                {
                    from = selected.Min(x => x.Frame);
                    to = selected.Max(x => x.Frame + x.Length);
                }
            }

            if (IsFromCurrentFrame)
            {
                int currentFrame = _currentTimeline.CurrentFrame;
                if (IsOnlySelectedItems)
                {
                    from = Math.Max(from, currentFrame);
                }
                else
                {
                    from = currentFrame;
                }
            }

            from = Math.Clamp(from, 0, Math.Max(0, _currentTimeline.Length - 1));
            to = Math.Clamp(to, from + 1, _currentTimeline.Length);

            try
            {
                // Mp4ConfigViewModel.EncodeFrom / EncodeTo (ReactiveProperty<int>)
                var vmType = _currentViewModel.GetType();
                var encodeFromProp = vmType.GetProperty("EncodeFrom");
                var encodeToProp = vmType.GetProperty("EncodeTo");

                var encodeFromObj = encodeFromProp?.GetValue(_currentViewModel);
                var encodeToObj = encodeToProp?.GetValue(_currentViewModel);

                if (encodeFromObj != null)
                {
                    var valProp = encodeFromObj.GetType().GetProperty("Value");
                    valProp?.SetValue(encodeFromObj, from);
                }

                if (encodeToObj != null)
                {
                    var valProp = encodeToObj.GetType().GetProperty("Value");
                    valProp?.SetValue(encodeToObj, to);
                }
            }
            catch (Exception ex)
            {
                VideoFileWriterPatch.Log($"UpdateEncodeRange error: {ex}");
            }

            try
            {
                var settingsType = Type.GetType("YukkuriMovieMaker.VideoFileWriter.VideoFileWriterSettings, YukkuriMovieMaker");
                if (settingsType != null)
                {
                    var defaultProp = settingsType.BaseType?.GetProperty("Default", BindingFlags.Public | BindingFlags.Static);
                    var settingsInst = defaultProp?.GetValue(null);
                    if (settingsInst != null)
                    {
                        settingsType.GetProperty("EncodeFrom")?.SetValue(settingsInst, from);
                        settingsType.GetProperty("EncodeTo")?.SetValue(settingsInst, to);
                    }
                }
            }
            catch { }
        }

        private static List<IItem> GetSelectedItems(Timeline timeline)
        {
            var list = new List<IItem>();
            try
            {
                if (timeline.SelectedItems != null && timeline.SelectedItems.Count > 0)
                {
                    list.AddRange(timeline.SelectedItems);
                }
                else if (timeline.SelectedItem != null)
                {
                    list.Add(timeline.SelectedItem);
                }
            }
            catch { }
            return list;
        }

        public static void ApplyItemHiding(Timeline? timeline)
        {
            if (!IsOnlySelectedItems || timeline == null) return;

            lock (_hiddenBackup)
            {
                _hiddenBackup.Clear();
                var selected = GetSelectedItems(timeline).ToHashSet();
                if (selected.Count == 0) return;

                foreach (var item in timeline.Items)
                {
                    _hiddenBackup[item] = item.IsHidden;
                    if (!selected.Contains(item))
                    {
                        item.IsHidden = true;
                    }
                }
                VideoFileWriterPatch.Log($"Applied item hiding: {selected.Count} selected items kept, {timeline.Items.Count - selected.Count} items hidden.");
            }
        }

        public static void RestoreItemHiding()
        {
            lock (_hiddenBackup)
            {
                if (_hiddenBackup.Count == 0) return;

                foreach (var (item, wasHidden) in _hiddenBackup)
                {
                    try
                    {
                        item.IsHidden = wasHidden;
                    }
                    catch { }
                }
                VideoFileWriterPatch.Log($"Restored item hiding for {_hiddenBackup.Count} items.");
                _hiddenBackup.Clear();
            }
        }

        public static UIElement WrapWithExtensionPanel(UIElement originalView)
        {
            var panel = new StackPanel { Orientation = Orientation.Vertical };
            panel.Children.Add(originalView);

            // YMM4 標準の設定項目と同じく、目立たない Expander + Label + 右寄せトグルで構成する
            var expander = new Expander
            {
                Header = "拡張出力設定",
                IsExpanded = true,
                Margin = new Thickness(0)
            };

            var content = new StackPanel { Orientation = Orientation.Vertical };

            string selectedDesc = "";
            if (_currentTimeline != null)
            {
                var sel = GetSelectedItems(_currentTimeline);
                if (sel.Count > 0)
                {
                    int min = sel.Min(x => x.Frame);
                    int max = sel.Max(x => x.Frame + x.Length);
                    selectedDesc = $"{sel.Count}個選択中: {min}f～{max}f";
                }
                else
                {
                    selectedDesc = "アイテム未選択";
                }
            }

            string selectedTip = "他のアイテムを非表示にし、出力範囲も選択アイテムに合わせます。"
                + (string.IsNullOrEmpty(selectedDesc) ? "" : " (" + selectedDesc + ")");

            content.Children.Add(CreateToggleRow(
                "選択したアイテムだけ出力",
                selectedTip,
                IsOnlySelectedItems,
                value =>
                {
                    IsOnlySelectedItems = value;
                    UpdateEncodeRange();
                }));

            string currentDesc = _currentTimeline != null
                ? $"現在位置: {_currentTimeline.CurrentFrame}f から末尾まで"
                : "現在のシークバー位置から末尾まで";

            content.Children.Add(CreateToggleRow(
                "シークバーの位置から出力",
                currentDesc,
                IsFromCurrentFrame,
                value =>
                {
                    IsFromCurrentFrame = value;
                    UpdateEncodeRange();
                }));

            expander.Content = content;
            panel.Children.Add(expander);

            return panel;
        }

        // 標準設定画面と同じ 1 行 (左にラベル、右にトグル) を作る
        private static UIElement CreateToggleRow(string label, string toolTip, bool value, Action<bool> onChanged)
        {
            var grid = new Grid();
            grid.Children.Add(new Label { Content = label, ToolTip = toolTip });
            grid.Children.Add(CreateToggleSlider(value, onChanged));
            return grid;
        }

        private static FrameworkElement CreateToggleSlider(bool value, Action<bool> onChanged)
        {
            var toggleType = Type.GetType("YukkuriMovieMaker.Controls.ToggleSlider, YukkuriMovieMaker.Plugin");
            if (toggleType != null)
            {
                try
                {
                    if (Activator.CreateInstance(toggleType) is FrameworkElement toggle)
                    {
                        toggle.HorizontalAlignment = HorizontalAlignment.Right;

                        var valueDp = toggleType.GetField("ValueProperty", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as DependencyProperty;
                        if (valueDp != null)
                        {
                            // 状態オブジェクト経由で双方向バインドする (イベント購読の解除漏れを避ける)
                            var state = new ToggleState(value, onChanged);
                            toggle.SetBinding(valueDp, new Binding(nameof(ToggleState.Value))
                            {
                                Source = state,
                                Mode = BindingMode.TwoWay
                            });
                        }

                        return toggle;
                    }
                }
                catch (Exception ex)
                {
                    VideoFileWriterPatch.Log($"Failed to create ToggleSlider: {ex.Message}");
                }
            }

            // ToggleSlider が使えない場合は標準の CheckBox で代替する
            var checkBox = new CheckBox
            {
                IsChecked = value,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 4, 4)
            };
            checkBox.Checked += (_, _) => onChanged(true);
            checkBox.Unchecked += (_, _) => onChanged(false);
            return checkBox;
        }

        // トグルの値をコールバックへ伝えるための状態オブジェクト
        private sealed class ToggleState : INotifyPropertyChanged
        {
            private readonly Action<bool> _onChanged;
            private bool _value;

            public ToggleState(bool initial, Action<bool> onChanged)
            {
                _value = initial;
                _onChanged = onChanged;
            }

            public bool Value
            {
                get => _value;
                set
                {
                    if (_value == value) return;
                    _value = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
                    _onChanged(value);
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
        }
    }
}
