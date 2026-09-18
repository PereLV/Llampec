namespace Llampec.Actions.AlwaysOnTop;

/// <summary>A nonactivating, click-through indicator owned by one pin.</summary>
public interface IAlwaysOnTopBorder : IDisposable
{
    void Update(int thickness);
}
