using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;

namespace CoreShift.Desktop;

internal enum BoardPart { Cpu, Graphics, Memory, Storage }

internal sealed class BoardViewport : Viewport3D
{
    private static readonly MeshGeometry3D Box = CreateBox();
    private static readonly MeshGeometry3D FanDisc = CreateFanDisc();
    private static readonly MeshGeometry3D FanBlade = Extrude([new(-.5, -.12), new(.3, -.48), new(.5, -.09), new(.24, .5), new(-.5, .19)]);
    private readonly Dictionary<GeometryModel3D, (BoardPart Part, Material Default)> parts = [];
    private readonly OrthographicCamera camera = new(new Point3D(0, 0, 15), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 12);
    private readonly AxisAngleRotation3D tilt = new(new Vector3D(1, 0, 0), 42);
    private readonly AxisAngleRotation3D turn = new(new Vector3D(0, 0, 1), -19);
    private readonly AxisAngleRotation3D fan = new(new Vector3D(0, 0, 1), 0);
    private readonly Material selectedMaterial;
    private readonly Material hoveredMaterial;
    private Point start;
    private Point previous;
    private Point lastHit = new(double.NegativeInfinity, double.NegativeInfinity);
    private bool dragging;
    private BoardPart? hovered;
    private BoardPart selected;
    private double zoom = 1;

    internal BoardViewport(Color accent, bool light, BoardPart selection)
    {
        selected = selection;
        selectedMaterial = MaterialFor(Mix(accent, Colors.White, light ? .27 : .12));
        hoveredMaterial = MaterialFor(Mix(accent, Colors.White, .4));
        ClipToBounds = true;
        Focusable = true;
        Camera = camera;
        if (RenderCapability.Tier < 0x20000 || !UiMotion.CanAnimate(this))
            RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        var scene = BuildBoard(accent, light);
        scene.Transform = new Transform3DGroup
        {
            Children = [new RotateTransform3D(turn), new RotateTransform3D(tilt)]
        };
        Children.Add(new ModelVisual3D
        {
            Content = new Model3DGroup
            {
                Children = [new AmbientLight(Color.FromRgb(179, 186, 191)),
                    new DirectionalLight(Colors.White, new Vector3D(-1, -2, -4)), scene]
            }
        });
        ColorParts();
        SizeChanged += (_, _) => FitCamera();
        IsVisibleChanged += (_, _) => { if (!IsVisible) SetHover(null); };
        MouseLeftButtonDown += BeginPointer;
        MouseMove += MovePointer;
        MouseLeftButtonUp += EndPointer;
        LostMouseCapture += (_, _) => dragging = false;
        MouseLeave += (_, _) => SetHover(null);
        MouseWheel += ZoomBoard;
        KeyDown += AdjustFromKeyboard;
    }

    internal event Action<BoardPart>? PartSelected;

    internal void Select(BoardPart part)
    {
        if (part == selected)
            return;
        selected = part;
        ColorParts();
    }

    internal void ResetView()
    {
        turn.Angle = -19;
        tilt.Angle = 42;
        zoom = 1;
        FitCamera();
    }

    internal void StopEffects() => SetHover(null);

    private void FitCamera()
    {
        if (ActualHeight > 0)
            camera.Width = Math.Max(12.35, 7.45 * ActualWidth / ActualHeight) * zoom;
    }

    private void BeginPointer(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ResetView();
            e.Handled = true;
            return;
        }
        start = previous = e.GetPosition(this);
        dragging = false;
        CaptureMouse();
        e.Handled = true;
    }

    private void MovePointer(object sender, MouseEventArgs e)
    {
        Point point = e.GetPosition(this);
        if (IsMouseCaptured)
        {
            if (!dragging && (point - start).LengthSquared < 16)
                return;
            dragging = true;
            SetHover(null);
            turn.Angle = (turn.Angle + (point.X - previous.X) * .52) % 360;
            tilt.Angle = Math.Clamp(tilt.Angle + (point.Y - previous.Y) * .45, -65, 68);
            previous = point;
            return;
        }

        if ((point - lastHit).LengthSquared < 36)
            return;
        lastHit = point;
        SetHover(Pick(point));
    }

    private void EndPointer(object sender, MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured)
            return;
        bool wasDrag = dragging;
        ReleaseMouseCapture();
        if (!wasDrag && Pick(e.GetPosition(this)) is { } part)
            PartSelected?.Invoke(part);
        e.Handled = true;
    }

    private void ZoomBoard(object sender, MouseWheelEventArgs e)
    {
        e.Handled = ChangeZoom(e.Delta > 0);
    }

    private bool ChangeZoom(bool closer)
    {
        double next = Math.Clamp(zoom * (closer ? .9 : 1.11), .66, 1.42);
        if (next == zoom)
            return false;
        zoom = next;
        FitCamera();
        SetHover(null);
        return true;
    }

    private void AdjustFromKeyboard(object sender, KeyEventArgs e)
    {
        if (!IsKeyboardFocused)
            return;
        switch (e.Key)
        {
            case Key.Left: turn.Angle = (turn.Angle - 9) % 360; break;
            case Key.Right: turn.Angle = (turn.Angle + 9) % 360; break;
            case Key.Up: tilt.Angle = Math.Clamp(tilt.Angle - 6, -65, 68); break;
            case Key.Down: tilt.Angle = Math.Clamp(tilt.Angle + 6, -65, 68); break;
            case Key.Add or Key.OemPlus: ChangeZoom(closer: true); break;
            case Key.Subtract or Key.OemMinus: ChangeZoom(closer: false); break;
            case Key.Home: ResetView(); break;
            default: return;
        }
        SetHover(null);
        e.Handled = true;
    }

    private BoardPart? Pick(Point point)
    {
        BoardPart? nearest = null;
        double distance = double.MaxValue;
        VisualTreeHelper.HitTest(this, null, result =>
        {
            if (result is RayMeshGeometry3DHitTestResult { ModelHit: GeometryModel3D model } mesh &&
                mesh.DistanceToRayOrigin < distance && parts.TryGetValue(model, out var hit))
            {
                nearest = hit.Part;
                distance = mesh.DistanceToRayOrigin;
            }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(point));
        return nearest;
    }

    private void SetHover(BoardPart? part)
    {
        if (hovered == part)
            return;
        hovered = part;
        Cursor = part.HasValue ? Cursors.Hand : Cursors.Arrow;
        ColorParts();
        DoubleAnimation? rotation = part == BoardPart.Graphics && TryFindResource("HoverEffects") is not false && UiMotion.CanAnimate(this)
            ? new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1175)) { RepeatBehavior = RepeatBehavior.Forever }
            : null;
        fan.BeginAnimation(AxisAngleRotation3D.AngleProperty, rotation);
    }

    private void ColorParts()
    {
        foreach (var (model, (part, material)) in parts)
        {
            var next = part == hovered ? hoveredMaterial : part == selected ? selectedMaterial : material;
            model.Material = model.BackMaterial = next;
        }
    }

    private Model3DGroup BuildBoard(Color accent, bool light)
    {
        var scene = new Model3DGroup();
        var substrate = MaterialFor(Mix(Color.FromRgb(26, 50, 48), accent, light ? .27 : .15));
        var trace = MaterialFor(Mix(Color.FromRgb(43, 77, 74), accent, .48));
        var casing = MaterialFor(Color.FromRgb(55, 68, 73));
        var dark = MaterialFor(Color.FromRgb(27, 35, 38));
        var metal = MaterialFor(Color.FromRgb(139, 165, 164));
        var contact = MaterialFor(Color.FromRgb(190, 152, 98));
        var rotor = MaterialFor(Mix(Color.FromRgb(134, 152, 156), accent, .28));

        void Place(double x, double y, double z, double w, double h, double d, Material material,
            BoardPart? part = null, Transform3D? rotation = null, MeshGeometry3D? shape = null)
        {
            var transform = new Transform3DGroup { Children = [new ScaleTransform3D(w, h, d), new TranslateTransform3D(x, y, z)] };
            if (rotation is not null)
                transform.Children.Add(rotation);
            var model = new GeometryModel3D(shape ?? Box, material) { BackMaterial = material, Transform = transform };
            scene.Children.Add(model);
            if (part is { } pickable)
                parts.Add(model, (pickable, material));
        }

        Place(0, 0, -.07, 9.7, 6.25, .18, substrate);
        for (int i = 0; i < 7; i++)
        {
            double y = .22 + i * .16;
            Place(-3.25, y, .03, 2.45, .026, .018, trace);
            Place(-2.01 + i * .15, y + .24, .03, .026, .48, .018, trace);
            Place(2.7 + i * .075, -1.48, .03, .028, 2.25, .018, trace);
        }
        for (int i = 0; i < 4; i++)
        {
            Place(-4.39, 2.03 - i * .57, .14, .65, .39, .38, metal);
            Place(4.42, -1.15 - i * .37, .14, .42, .22, .35, casing);
        }
        foreach (double x in new[] { -4.55, 4.55 })
            foreach (double y in new[] { -2.81, 2.81 })
                Place(x, y, .03, .19, .19, .05, contact);

        Place(-.96, 1.47, .11, 2.19, 2.12, .25, casing, BoardPart.Cpu);
        Place(-.96, 1.47, .28, 1.71, 1.63, .13, metal, BoardPart.Cpu);
        Place(-.96, 1.47, .358, 1.23, 1.18, .037, casing, BoardPart.Cpu);
        for (int i = 0; i < 8; i++)
        {
            double offset = (i - 3.5) * .222;
            Place(-.96 + offset, .49, .21, .063, .12, .043, contact);
            Place(-.96 + offset, 2.45, .21, .063, .12, .043, contact);
        }

        for (int stick = 0; stick < 2; stick++)
        {
            double x = 1.22 + stick * .57;
            Place(x, 1.45, .21, .39, 2.7, .36, casing, BoardPart.Memory);
            Place(x, 1.45, .43, .32, 2.48, .16, trace, BoardPart.Memory);
            for (int chip = 0; chip < 5; chip++)
                Place(x, .52 + chip * .44, .52, .225, .29, .05, metal, BoardPart.Memory);
        }

        Place(3.29, .45, .15, 2.16, .56, .22, casing, BoardPart.Storage);
        Place(3.22, .45, .3, 1.68, .43, .11, trace, BoardPart.Storage);
        Place(3.42, .45, .38, .67, .32, .05, metal, BoardPart.Storage);
        Place(4.21, .45, .27, .26, .38, .075, contact, BoardPart.Storage);

        Place(-1.56, -1.73, .26, 4.81, 1.74, .47, casing, BoardPart.Graphics);
        Place(-1.54, -1.73, .51, 4.25, 1.5, .15, substrate, BoardPart.Graphics);
        Place(-1.56, -2.58, .25, 2.59, .20, .11, contact, BoardPart.Graphics);
        Place(-3.98, -1.73, .18, .18, 2.04, .51, metal, BoardPart.Graphics);
        foreach (double x in new[] { -2.65, -.65 })
        {
            Place(x, -1.73, .60, 1.37, 1.37, .06, metal, shape: FanDisc);
            Place(x, -1.73, .644, 1.23, 1.23, .04, dark, shape: FanDisc);
            var rotate = new RotateTransform3D(fan, x, -1.73, 0);
            for (int blade = 0; blade < 7; blade++)
            {
                var transform = new Transform3DGroup
                {
                    Children = [new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), blade * 360d / 7), x, -1.73, 0), rotate]
                };
                Place(x + .31, -1.73, .69, .45, .222, .06, rotor, rotation: transform, shape: FanBlade);
            }
            Place(x, -1.73, .707, .27, .27, .085, metal, shape: FanDisc);
        }
        return scene;
    }

    private static DiffuseMaterial MaterialFor(Color color)
    {
        var material = new DiffuseMaterial(new SolidColorBrush(color));
        material.Freeze();
        return material;
    }

    private static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * amount), (byte)(a.G + (b.G - a.G) * amount), (byte)(a.B + (b.B - a.B) * amount));

    private static MeshGeometry3D CreateFanDisc()
    {
        const int count = 24;
        var outline = new Point[count];
        for (int i = 0; i < count; i++)
        {
            double angle = i * 2 * Math.PI / count;
            outline[i] = new Point(.5 * Math.Cos(angle), .5 * Math.Sin(angle));
        }
        return Extrude(outline);
    }

    private static MeshGeometry3D Extrude(Point[] outline)
    {
        var mesh = new MeshGeometry3D();
        for (int i = 0; i < outline.Length; i++)
        {
            Point point = outline[i];
            mesh.Positions.Add(new Point3D(point.X, point.Y, .5));
            if (i > 0 && i < outline.Length - 1)
            {
                mesh.TriangleIndices.Add(0);
                mesh.TriangleIndices.Add(i);
                mesh.TriangleIndices.Add(i + 1);
            }
        }
        for (int i = 0; i < outline.Length; i++)
        {
            Point a = outline[i];
            Point b = outline[(i + 1) % outline.Length];
            int start = mesh.Positions.Count;
            mesh.Positions.Add(new Point3D(a.X, a.Y, -.5));
            mesh.Positions.Add(new Point3D(b.X, b.Y, -.5));
            mesh.Positions.Add(new Point3D(b.X, b.Y, .5));
            mesh.Positions.Add(new Point3D(a.X, a.Y, .5));
            foreach (int vertex in new[] { 0, 1, 2, 0, 2, 3 })
                mesh.TriangleIndices.Add(start + vertex);
        }
        mesh.Freeze();
        return mesh;
    }

    private static MeshGeometry3D CreateBox()
    {
        // Separate face vertices keep the edges crisp when the board rotates.
        Point3D[] corners =
        [
            new(-.5, -.5, -.5), new(.5, -.5, -.5), new(.5, .5, -.5), new(-.5, .5, -.5),
            new(-.5, -.5, .5), new(.5, -.5, .5), new(.5, .5, .5), new(-.5, .5, .5)
        ];
        int[][] faces = [[3, 2, 1, 0], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
        var mesh = new MeshGeometry3D();
        foreach (int[] face in faces)
        {
            int offset = mesh.Positions.Count;
            foreach (int corner in face)
                mesh.Positions.Add(corners[corner]);
            foreach (int index in new[] { 0, 1, 2, 0, 2, 3 })
                mesh.TriangleIndices.Add(offset + index);
        }
        mesh.Freeze();
        return mesh;
    }
}
