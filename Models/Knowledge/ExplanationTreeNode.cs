using System.Collections.Immutable;

namespace IotExpertApp.Models.Knowledge;

// узлы содержат только представление закрепленного результата, без Avalonia и повторного вывода
public sealed record ExplanationTreeNode(string Title, ImmutableArray<ExplanationTreeNode> Children);
