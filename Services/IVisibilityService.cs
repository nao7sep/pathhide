using PathHide.Models;

namespace PathHide.Services;

public interface IVisibilityService
{
    PathInspection Inspect(string path);
    void Hide(string path);
    void Show(string path);
}
