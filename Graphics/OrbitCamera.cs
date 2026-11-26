using Microsoft.Xna.Framework;

namespace MeshViewer;

/// <summary>
/// Орбит-камера вокруг точки: азимут, наклон и расстояние. Всё движение камеры идёт через Rotate/Zoom/Pan —
/// сглаживание или автоповорот, если понадобятся, добавятся здесь и не тронут остальное.
/// </summary>
public sealed class OrbitCamera
{
    public const float FieldOfView = MathHelper.Pi / 4;

    const float TurnSpeed = MathHelper.TwoPi / 1000;       // полный оборот — тысяча пикселей мыши
    const float ZoomStep = 1.15f;                          // на одну щелчку колеса (120 единиц)
    const float MaxPitch = MathHelper.PiOver2 - 0.02f;

    public Vector3 Target { get; set; }

    /// <summary>Азимут в радианах: 0 — камера на +Z, перед лицом модели (у glTF лицо смотрит в +Z).</summary>
    public float Yaw { get; set; }

    /// <summary>Наклон в радианах: больше нуля — смотрим сверху.</summary>
    public float Pitch { get; set; }

    public float Distance { get; set; }

    /// <summary>Радиус вписанной сферы — от него считаются ближняя и дальняя плоскости и пределы масштаба.</summary>
    float _radius = 1f;

    public Vector3 Eye => Target + Distance * new Vector3(MathF.Cos(Pitch) * MathF.Sin(Yaw), MathF.Sin(Pitch), MathF.Cos(Pitch) * MathF.Cos(Yaw));
    public Vector3 Forward => Vector3.Normalize(Target - Eye);
    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.Up));
    public Vector3 Up => Vector3.Cross(Right, Forward);
    public Matrix View => Matrix.CreateLookAt(Eye, Target, Vector3.Up);

    /// <summary>Проекция под текущее отношение сторон кадра — его знает окно, не камера.</summary>
    public Matrix Projection(float aspect) => Matrix.CreatePerspectiveFieldOfView(FieldOfView, aspect, MathF.Max(Distance * 0.02f, 1e-4f), Distance + _radius * 4f);

    /// <summary>Вписывает габариты в кадр с запасом.</summary>
    public void Frame(BoundingBox bounds, float aspect)
    {
        Vector3 size = bounds.Max - bounds.Min;
        _radius = MathF.Max(size.Length() * 0.5f, 1e-3f);
        // вписываем в тот угол поля зрения, что уже (у широкого окна это вертикальный)
        float half = MathF.Min(FieldOfView, MathF.Atan(MathF.Tan(FieldOfView / 2) * aspect)) / 2;
        Target = (bounds.Min + bounds.Max) * 0.5f;
        Distance = _radius / MathF.Sin(half) * 1.1f;
    }

    /// <summary>Мышь тянет модель за видимую сторону: вправо — лицо уходит вправо, вниз — наклоняется к нам.</summary>
    public void Rotate(float dx, float dy)
    {
        Yaw -= dx * TurnSpeed;
        Pitch = Math.Clamp(Pitch + dy * TurnSpeed, -MaxPitch, MaxPitch);
    }

    public void Zoom(float wheel)
    {
        Distance = Math.Clamp(Distance * MathF.Pow(ZoomStep, -wheel / 120f), _radius * 0.05f, _radius * 50f);
    }

    /// <summary>Сдвигает точку взгляда на столько пикселей, сколько прошла мышь.</summary>
    public void Pan(float dx, float dy, float viewportHeight)
    {
        float unit = 2f * Distance * MathF.Tan(FieldOfView / 2) / MathF.Max(viewportHeight, 1);
        Target += (Up * dy - Right * dx) * unit;
    }
}
