using System;
using System.Collections.Generic;
using System.Linq;
using Apogee.DevTools;
using Apogee.Native.ImNodes;
using Apogee.Rendering.Graph;

namespace Apogee.Editor.Panels;

/// <summary>A pin of a pass: one resource it reads (input) or writes (output).</summary>
public sealed record GraphPin(int Id, string Resource);

/// <summary>A pass of the render graph laid out as a node: its column is how many passes it waits for in a row, its row the place among the passes of that column.</summary>
public sealed record GraphNode(int Id, string Name, int Column, int Row, IReadOnlyList<GraphPin> Inputs, IReadOnlyList<GraphPin> Outputs);

/// <summary>A resource handed from the pass that last wrote it to a pass that reads it.</summary>
public sealed record GraphLink(int Id, int FromPin, int ToPin);

/// <summary>The render graph's plan as nodes and links: what the render graph panel draws, with no ImGui in it so that it is tested on its own.</summary>
public static class RenderGraphLayout
{
    public static (List<GraphNode> Nodes, List<GraphLink> Links) Build(GraphPlan plan)
    {
        var nodes = new List<GraphNode>();
        var links = new List<GraphLink>();
        var lastWriter = new Dictionary<string, (int Node, int Pin)>(StringComparer.Ordinal);
        var column = new Dictionary<int, int>();
        var rowsInColumn = new Dictionary<int, int>();
        int index = 0;
        foreach (PassPlan pass in plan.Passes.Where(p => !p.Culled))
        {
            int id = ++index;
            var inputs = new List<GraphPin>();
            var outputs = new List<GraphPin>();
            int level = 0;
            foreach (string resource in pass.Reads.Distinct(StringComparer.Ordinal).Take(49))
            {
                var pin = new GraphPin((id * 100) + inputs.Count, resource);
                inputs.Add(pin);
                if (lastWriter.TryGetValue(resource, out (int Node, int Pin) writer))
                {
                    links.Add(new GraphLink(links.Count + 1, writer.Pin, pin.Id));
                    level = Math.Max(level, column[writer.Node] + 1);
                }
            }

            foreach (string resource in pass.Writes.Distinct(StringComparer.Ordinal).Take(49))
            {
                var pin = new GraphPin((id * 100) + 50 + outputs.Count, resource);
                outputs.Add(pin);
                lastWriter[resource] = (id, pin.Id);
            }

            column[id] = level;
            int row = rowsInColumn.GetValueOrDefault(level);
            rowsInColumn[level] = row + 1;
            nodes.Add(new GraphNode(id, pass.Name, level, row, inputs, outputs));
        }

        return (nodes, links);
    }
}

/// <summary>The passes of the last frame's render graph as a read-only node view (imnodes): which pass writes what and which reads it.</summary>
public sealed class RenderGraphPanel
{
    private string _positionedFor = "";

    public void Draw(EditorContext ctx)
    {
        if (!Ui.Begin("Render graph"))
        {
            Ui.End();
            return;
        }

        if (ctx.Plan is not { } plan)
        {
            Ui.Text("No frame has been drawn yet.");
            Ui.End();
            return;
        }

        (List<GraphNode> nodes, List<GraphLink> links) = RenderGraphLayout.Build(plan);
        string signature = string.Join('|', nodes.Select(n => n.Name));
        bool place = signature != _positionedFor;
        _positionedFor = signature;

        Nodes.BeginNodeEditor();
        foreach (GraphNode node in nodes)
        {
            Nodes.BeginNode(node.Id);
            Nodes.BeginNodeTitleBar();
            Ui.Text(node.Name);
            Nodes.EndNodeTitleBar();
            foreach (GraphPin pin in node.Inputs)
            {
                Nodes.BeginInputAttribute(pin.Id, (int)AnodesPinShape.CircleFilled);
                Ui.Text(pin.Resource);
                Nodes.EndInputAttribute();
            }

            foreach (GraphPin pin in node.Outputs)
            {
                Nodes.BeginOutputAttribute(pin.Id, (int)AnodesPinShape.QuadFilled);
                Ui.Text(pin.Resource);
                Nodes.EndOutputAttribute();
            }

            Nodes.EndNode();
            if (place)
            {
                Nodes.SetNodeGridSpacePos(node.Id, 20 + (node.Column * 230), 20 + (node.Row * 150));
            }
        }

        foreach (GraphLink link in links)
        {
            Nodes.Link(link.Id, link.FromPin, link.ToPin);
        }

        Nodes.Minimap(0.2f, (int)AnodesMiniMapLocation.BottomRight);
        Nodes.EndNodeEditor();
        Ui.End();
    }
}
