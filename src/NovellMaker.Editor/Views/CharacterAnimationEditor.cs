using System.Collections.Immutable;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NovellMaker.Core.Characters;
using NovellMaker.Core.Execution;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Views;

/// <summary>Small numeric rig/timeline editor; every Apply is a single session undo operation.</summary>
public sealed class CharacterAnimationEditor : UserControl
{
    private sealed record Option(Guid? Id, string Name) { public override string ToString() => Name; }
    private EditorSession? session;
    private Guid? stateId, partId, clipId;
    private double cursor;
    private bool building, playing;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly CharacterVisualView preview = new() { Height = 260, IsHitTestVisible = false };
    private Slider? scrubber;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Tan };
    private DateTime lastTick;
    private int playedRepeats;

    public CharacterAnimationEditor()
    {
        DataContextChanged += (_, _) => Connect();
        timer.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;
            cursor += (now - lastTick).TotalSeconds; lastTick = now;
            var clip = Visual?.Clips.FirstOrDefault(c => c.Id == clipId);
            if (clip is null) { Stop(); return; }
            if (cursor >= clip.Duration)
            { if (clip.Loop && (clip.RepeatCount == 0 || playedRepeats++ < clip.RepeatCount)) cursor %= clip.Duration; else { cursor = clip.Duration; Stop(); } }
            if (scrubber is not null) scrubber.Value = cursor;
            Present();
        };
    }

    private CharacterVisual? Visual => session?.SelectedState?.Model.Visual;
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); Connect(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { Stop(); if (session is not null) session.PropertyChanged -= Changed; session = null; base.OnDetachedFromVisualTree(e); }

    private void Connect()
    {
        if (session is not null) session.PropertyChanged -= Changed;
        session = DataContext as EditorSession;
        if (session is not null) session.PropertyChanged += Changed;
        Rebuild();
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorSession.SelectedState) or nameof(EditorSession.CanEditState) or nameof(EditorSession.AssetRevision)) Rebuild();
    }

    private void Stop() { playing = false; timer.Stop(); }
    private void Commit(CharacterVisual? visual)
    {
        try { Stop(); visual?.Validate(); session?.SetCharacterVisual(visual); status.Text = ""; Rebuild(); }
        catch (ArgumentException error) { status.Text = error.Message; }
    }

    private void Present()
    {
        if (session?.SelectedState is not { } state || session.Project is not { } project) return;
        var image = project.Images.FirstOrDefault(i => i.Id == state.ImageId);
        if (image is null) return;
        preview.Present(new(session.SelectedCharacter!.Id, "", state.Id, image)
        { Visual = Visual is { } visual ? visual with { DefaultClipId = clipId } : null, AnimationTime = cursor, Assets = project.Images },
            session.ProjectDirectory, session.AssetRevision);
    }

    private void Rebuild()
    {
        if (building) return;
        building = true;
        try
        {
            if (stateId != session?.SelectedState?.Id) { stateId = session?.SelectedState?.Id; partId = clipId = null; cursor = 0; Stop(); }
            if (preview.Parent is Border oldPreview) oldPreview.Child = null;
            if (status.Parent is Panel oldStatus) oldStatus.Children.Remove(status);
            var body = new StackPanel { Spacing = 8, IsEnabled = session?.CanEdit == true };
            Content = body;
            body.Children.Add(new TextBlock { Text = "Части и анимация", FontSize = 20, Margin = new Thickness(0, 16, 0, 0) });
            body.Children.Add(new TextBlock { Text = "Изменения применяются кнопками и отменяются через Ctrl+Z. Поза запускает свой клип по умолчанию.", TextWrapping = TextWrapping.Wrap });
            var actions = Row(body);
            Button(actions, "Импорт GIF…", async () => { if (session is not null) await session.ImportCharacterGifAsync(); Rebuild(); });
            Button(actions, "Добавить готовый пример", async () => { if (session is not null) await session.AddCharacterAnimationExampleAsync(); });
            if (Visual is not { } visual)
            {
                Button(actions, "Создать из изображения", () =>
                {
                    if (session?.SelectedState is not { } state || session.Project?.Images.FirstOrDefault(i => i.Id == state.ImageId) is not { } image) return;
                    var part = new CharacterPart(Guid.NewGuid(), "Основа", image.Id, null, 0, 0, 0, new());
                    partId = part.Id;
                    Commit(new(image.PixelWidth, image.PixelHeight, [part], [], null));
                });
                return;
            }
            Button(actions, "Статическое изображение", () => Commit(null));
            var size = Row(body);
            var width = Number(size, "Холст W", visual.Width, 1, 8192);
            var height = Number(size, "H", visual.Height, 1, 8192);
            Button(size, "Размер", () => Commit(visual with { Width = (int)width.Value!, Height = (int)height.Value! }));
            var previewHost = new Border { Background = new SolidColorBrush(Color.Parse("#17191D")), Child = preview, ClipToBounds = true };
            body.Children.Add(previewHost);

            if (!visual.Clips.Any(c => c.Id == clipId)) clipId = visual.DefaultClipId ?? visual.Clips.FirstOrDefault()?.Id;
            if (!visual.Parts.Any(p => p.Id == partId)) partId = visual.Parts.FirstOrDefault()?.Id;
            var clips = Row(body);
            var clipBox = Options(clips, "Клип", visual.Clips.Select(c => new Option(c.Id, c.Name)), clipId);
            clipBox.SelectionChanged += (_, _) => { if (!building) { clipId = (clipBox.SelectedItem as Option)?.Id; cursor = 0; Stop(); Rebuild(); } };
            Button(clips, "+ Клип", () =>
            { var clip = new CharacterClip(Guid.NewGuid(), $"Клип {visual.Clips.Length + 1}", 1, true, [], []); clipId = clip.Id; Commit(visual with { Clips = visual.Clips.Add(clip), DefaultClipId = visual.DefaultClipId ?? clip.Id }); });
            var selectedClip = visual.Clips.FirstOrDefault(c => c.Id == clipId);
            if (selectedClip is not null)
            {
                var clip = selectedClip;
                var clipRow = Row(body);
                var name = Text(clipRow, "Имя", clip.Name);
                var duration = Number(clipRow, "Секунды", clip.Duration, 0.01, 3600);
                duration.IsEnabled = clip.Frames.IsEmpty;
                var loop = new CheckBox { Content = "Повтор", IsChecked = clip.Loop, VerticalAlignment = VerticalAlignment.Center };
                clipRow.Children.Add(loop);
                var repeats = Number(clipRow, "Повторов (0 = ∞)", clip.RepeatCount, 0, 65535);
                Button(clipRow, "Применить клип", () => ReplaceClip(visual, clip with { Name = name.Text ?? "", Duration = (double)duration.Value!, Loop = loop.IsChecked == true, RepeatCount = (int)repeats.Value! }));
                var clipActions = Row(body);
                Button(clipActions, visual.DefaultClipId == clip.Id ? "Клип по умолчанию" : "Запускать с позой", () => Commit(visual with { DefaultClipId = clip.Id }));
                Button(clipActions, "Удалить клип", () => { var rest = visual.Clips.Remove(clip); clipId = rest.FirstOrDefault()?.Id; Commit(visual with { Clips = rest, DefaultClipId = visual.DefaultClipId == clip.Id ? clipId : visual.DefaultClipId }); });
                var timeline = Row(body);
                Button(timeline, "Пуск / пауза", () => { if (playing) Stop(); else { if (cursor >= clip.Duration) { cursor = 0; playedRepeats = 0; } playing = true; lastTick = DateTime.UtcNow; timer.Start(); } });
                scrubber = new Slider { Minimum = 0, Maximum = clip.Duration, Value = Math.Clamp(cursor, 0, clip.Duration), Width = 260 };
                timeline.Children.Add(scrubber);
                var time = new TextBlock { Text = $"{cursor:0.00} с", VerticalAlignment = VerticalAlignment.Center };
                timeline.Children.Add(time);
                scrubber.ValueChanged += (_, _) => { cursor = scrubber.Value; time.Text = $"{cursor:0.00} с"; Present(); };
                BuildFrames(body, visual, clip);
            }
            BuildParts(body, visual, selectedClip);
            body.Children.Add(status);
            Present();
        }
        finally { building = false; }
    }

    private void BuildParts(StackPanel body, CharacterVisual visual, CharacterClip? clip)
    {
        var row = Row(body);
        var partBox = Options(row, "Часть / слой", visual.Parts.Select(p => new Option(p.Id, $"{p.Layer}: {p.Name}")), partId);
        partBox.SelectionChanged += (_, _) => { if (!building) { partId = (partBox.SelectedItem as Option)?.Id; Rebuild(); } };
        Button(row, "+ Часть", () =>
        {
            if (session?.Project?.Images.FirstOrDefault() is not { } image) return;
            var part = new CharacterPart(Guid.NewGuid(), $"Часть {visual.Parts.Length + 1}", image.Id, null, 0, 0, visual.Parts.Length, new());
            partId = part.Id; Commit(visual with { Parts = visual.Parts.Add(part) });
        });
        var selectedPart = visual.Parts.FirstOrDefault(p => p.Id == partId);
        if (selectedPart is null) return;
        var part = selectedPart;
        var identity = Row(body);
        var name = Text(identity, "Имя части", part.Name);
        var imageBox = Options(identity, "Изображение", session!.Project!.Images.Select(i => new Option(i.Id, i.Name)), part.ImageId);
        var parentRow = Row(body);
        var parentBox = Options(parentRow, "Родитель", new[] { new Option(null, "Холст") }.Concat(visual.Parts.Where(p => p.Id != part.Id).Select(p => new Option(p.Id, p.Name))), part.ParentId);
        var layer = Number(parentRow, "Слой", part.Layer, -1000, 1000);
        var pivot = Row(body);
        var px = Number(pivot, "Pivot X (px)", part.PivotX, -8192, 8192);
        var py = Number(pivot, "Pivot Y (px)", part.PivotY, -8192, 8192);
        var keys = clip?.Tracks.FirstOrDefault(t => t.PartId == part.Id)?.Keys ?? [];
        var keyRow = Row(body);
        keyRow.Children.Add(new TextBlock { Text = "Ключи (секунды)", VerticalAlignment = VerticalAlignment.Center });
        var keyBox = new ComboBox { Width = 180, ItemsSource = keys.Select(k => k.Time).ToArray() };
        keyRow.Children.Add(keyBox);
        keyBox.SelectionChanged += (_, _) =>
        { if (!building && keyBox.SelectedItem is double time) { cursor = time; Rebuild(); } };
        var key = keys.FirstOrDefault(k => Math.Abs(k.Time - cursor) < 0.000001);
        var t = key?.Transform ?? part.Transform;
        var position = Row(body);
        var x = Number(position, "X (px)", t.X, -32768, 32768);
        var y = Number(position, "Y (px)", t.Y, -32768, 32768);
        var rotation = Number(position, "Угол °", t.Rotation, -36000, 36000);
        var scales = Row(body);
        var sx = Number(scales, "Масштаб X", t.ScaleX, 0.01, 10);
        var sy = Number(scales, "Масштаб Y", t.ScaleY, 0.01, 10);
        var opacity = Number(scales, "Непрозрачность", t.Opacity, 0, 1);
        PartTransform Transform() => new((double)x.Value!, (double)y.Value!, (double)rotation.Value!, (double)sx.Value!, (double)sy.Value!, (double)opacity.Value!);
        var apply = Row(body);
        Button(apply, "Применить часть (базовая поза)", () => Commit(visual with { Parts = visual.Parts.Replace(part, part with
        { Name = name.Text ?? "", ImageId = (imageBox.SelectedItem as Option)?.Id ?? part.ImageId, ParentId = (parentBox.SelectedItem as Option)?.Id,
            Layer = (int)layer.Value!, PivotX = (double)px.Value!, PivotY = (double)py.Value!, Transform = Transform() }) }));
        Button(apply, "Удалить часть и потомков", () =>
        {
            var ids = new HashSet<Guid> { part.Id };
            for (var i = 0; i < visual.Parts.Length; i++) foreach (var child in visual.Parts.Where(p => p.ParentId is { } parent && ids.Contains(parent))) ids.Add(child.Id);
            Commit(visual with { Parts = [.. visual.Parts.Where(p => !ids.Contains(p.Id))], Clips = [.. visual.Clips.Select(c => c with { Tracks = [.. c.Tracks.Where(t => !ids.Contains(t.PartId))] })] });
        });
        if (clip is null || !clip.Frames.IsEmpty) return;
        var editKey = Row(body);
        var keyTime = Number(editKey, "Время ключа, с", cursor, 0, clip.Duration);
        var hold = new CheckBox { Content = "Ступенька", IsChecked = key?.Hold ?? false };
        editKey.Children.Add(hold);
        Button(editKey, "Записать ключ", () =>
        {
            cursor = (double)keyTime.Value!;
            var replacement = new PartKeyframe(cursor, Transform(), (imageBox.SelectedItem as Option)?.Id, hold.IsChecked == true);
            var track = new PartTrack(part.Id, [.. keys.Where(k => Math.Abs(k.Time - cursor) > 0.000001).Append(replacement).OrderBy(k => k.Time)]);
            ReplaceClip(visual, clip with { Tracks = [.. clip.Tracks.Where(t => t.PartId != part.Id).Append(track)] });
        });
        Button(editKey, "Удалить ключ", () =>
        {
            var remaining = keys.Where(k => Math.Abs(k.Time - cursor) > 0.000001).ToImmutableArray();
            var tracks = clip.Tracks.Where(t => t.PartId != part.Id);
            if (!remaining.IsEmpty) tracks = tracks.Append(new(part.Id, remaining));
            ReplaceClip(visual, clip with { Tracks = [.. tracks] });
        });
    }

    private void BuildFrames(StackPanel body, CharacterVisual visual, CharacterClip clip)
    {
        var row = Row(body);
        var imageBox = Options(row, "Покадровый клип", session!.Project!.Images.Select(i => new Option(i.Id, i.Name)), session.SelectedState?.ImageId);
        var duration = Number(row, "Длина кадра, с", 0.1, 0.01, 3600);
        Button(row, "+ Кадр", () =>
        {
            if (imageBox.SelectedItem is not Option { Id: { } image }) return;
            var frames = clip.Frames.Add(new(image, (double)duration.Value!));
            ReplaceClip(visual, clip with { Frames = frames, Duration = frames.Sum(f => f.Duration), Tracks = [] });
        }, clip.Tracks.IsEmpty);
        if (clip.Frames.IsEmpty) return;
        var frameBox = new ComboBox { ItemsSource = clip.Frames.Select((f, i) => new Option(new Guid(i + 1, 0, 0, new byte[8]), $"{i + 1}: {f.Duration:0.00} с")).ToArray(), SelectedIndex = 0, Width = 150 };
        var framesRow = Row(body); framesRow.Children.Add(frameBox);
        void Modify(Func<ImmutableArray<CharacterFrame>, int, ImmutableArray<CharacterFrame>> change)
        {
            if (frameBox.SelectedIndex < 0) return;
            var frames = change(clip.Frames, frameBox.SelectedIndex);
            ReplaceClip(visual, clip with { Frames = frames, Duration = frames.IsEmpty ? 1 : frames.Sum(f => f.Duration) });
        }
        Button(framesRow, "Заменить кадр / длительность", () => Modify((frames, i) => frames.SetItem(i, new((imageBox.SelectedItem as Option)?.Id ?? frames[i].ImageId, (double)duration.Value!))));
        Button(framesRow, "Раньше", () => Modify((frames, i) => i == 0 ? frames : frames.SetItem(i - 1, frames[i]).SetItem(i, frames[i - 1])));
        Button(framesRow, "Удалить кадр", () => Modify((frames, i) => frames.RemoveAt(i)));
    }

    private void ReplaceClip(CharacterVisual visual, CharacterClip clip) => Commit(visual with { Clips = visual.Clips.SetItem(visual.Clips.IndexOf(visual.Clips.First(c => c.Id == clip.Id)), clip) });
    private static WrapPanel Row(StackPanel body) { var row = new WrapPanel { Orientation = Orientation.Horizontal }; body.Children.Add(row); return row; }
    private static void Button(Panel row, string title, Action action, bool enabled = true)
    { var button = new Button { Content = title, Margin = new Thickness(0, 3, 8, 3), IsEnabled = enabled }; button.Click += (_, _) => action(); row.Children.Add(button); }
    private static void Button(Panel row, string title, Func<Task> action)
    { var button = new Button { Content = title, Margin = new Thickness(0, 3, 8, 3) }; button.Click += async (_, _) => await action(); row.Children.Add(button); }
    private static NumericUpDown Number(Panel row, string label, double value, double min, double max)
    { var box = new NumericUpDown { Name = label switch { "Pivot X (px)" => "PartPivotX", "Pivot Y (px)" => "PartPivotY", "Угол °" => "PartRotation", "Время ключа, с" => "PartKeyTime", _ => null }, Value = (decimal)Math.Clamp(value, min, max), Minimum = (decimal)min, Maximum = (decimal)max, Increment = 0.1m, Width = 120, FormatString = "0.##" }; Field(row, label, box); return box; }
    private static TextBox Text(Panel row, string label, string value)
    { var box = new TextBox { Text = value, Width = 160 }; Field(row, label, box); return box; }
    private static ComboBox Options(Panel row, string label, IEnumerable<Option> values, Guid? id)
    { var options = values.ToArray(); var box = new ComboBox { ItemsSource = options, SelectedItem = options.FirstOrDefault(o => o.Id == id), Width = 190 }; Field(row, label, box); return box; }
    private static void Field(Panel row, string label, Control input)
    { var field = new StackPanel { Spacing = 3, Margin = new Thickness(0, 3, 8, 3) }; field.Children.Add(new TextBlock { Text = label, FontSize = 12 }); field.Children.Add(input); row.Children.Add(field); }
}
