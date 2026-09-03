namespace Fortiq.Application;

public interface IKeyLease : IDisposable
{
    int Length { get; }

    void CopyTo(Span<byte> destination);
}
