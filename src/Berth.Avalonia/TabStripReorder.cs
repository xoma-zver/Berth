using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Berth.Controls;

/// <summary>One tab header of a strip band as the target catalog captured it: the live control plus its natural bounds in gesture coordinates (DA-9.7 v0.18).</summary>
internal readonly record struct StripHeaderView(DockTabHeader Header, Rect Rect);

/// <summary>
/// One strip band of the target catalog (DA-9.7 v0.18): the band control — a group's
/// tab bar or the decorator header row hosting a panel root group's strip (TW-9.5) — with
/// its rectangle in gesture coordinates and its headers in visual order. Captured per
/// catalog build; an external re-projection rebuilds the catalog over fresh leaf chrome,
/// which is the reapply moment of the reorder overrides (the section 12 contract of
/// tool-windows).
/// </summary>
internal sealed record StripBandView(Control Band, Rect Rect, ImmutableArray<StripHeaderView> Headers);

/// <summary>
/// The live reorder-preview payload of one strip insertion zone (DA-9.7 v0.18): the
/// receiving band with the encoded insertion predecessor, plus the donor band holding the
/// dragged tab's header when that is a different band — its collapse travels with every
/// strip zone of a cross-strip hover. Pure catalog data; applying and clearing the visual
/// overrides is the job of <see cref="StripReorderOverrides"/>.
/// </summary>
internal sealed record StripReorderPreview(
    string DraggedId, StripBandView Receiver, string? PredecessorId, StripBandView? Donor);

/// <summary>
/// Override engine of the live strip reorder preview (DA-9.7 v0.18): over a strip
/// insertion zone the headers move apart and the dragged header's place collapses — in the
/// receiving band, and in the donor band of a cross-strip hover alike. Everything is a pure
/// visual override of leaf chrome (the section 12 contract of tool-windows): shifts are
/// RenderTransforms and the collapse is opacity, so <c>Bounds</c> never change and the
/// hit-test zone geometry stays stable; the real headers are never reordered, so
/// cancellation resets the overrides and leaves no trace (DA-E22 by construction — unlike
/// the reference, which mutates the real order mid-gesture). The placeholder rectangle —
/// the gap the headers opened, the place the tab takes on release — is computed here in
/// gesture coordinates and rendered by the gesture visual as a framed fill; it is clipped
/// into the band, like the band clips its own overflowing headers (overflow itself stays
/// unhandled — document-area, section 11).
/// </summary>
internal sealed class StripReorderOverrides
{
    private readonly List<DockTabHeader> _shifted = [];
    private readonly List<DockTabHeader> _collapsed = [];
    private StripReorderPreview? _applied;
    private Rect _placeholderRect;

    /// <summary>
    /// Applies the overrides of one strip zone and returns the insertion placeholder
    /// rectangle in gesture coordinates — empty when the band clips it away entirely.
    /// Idempotent per payload instance: the same zone of the same catalog re-applies
    /// nothing, while a rebuilt catalog delivers a fresh payload, which re-lays the
    /// overrides over the fresh headers (the reapply of the section 12 contract).
    /// </summary>
    public Rect Apply(StripReorderPreview preview, double placeholderWidth)
    {
        if (ReferenceEquals(_applied, preview))
        {
            return _placeholderRect;
        }

        Clear();
        _applied = preview;
        _placeholderRect = LayOutReceiver(preview, placeholderWidth);
        if (preview.Donor is { } donor)
        {
            CollapseRun(donor, preview.DraggedId);
        }

        return _placeholderRect;
    }

    /// <summary>Resets every override: transforms and opacity return to the projection's own values (DA-E22).</summary>
    public void Clear()
    {
        foreach (var header in _shifted)
        {
            header.RenderTransform = null;
        }

        foreach (var header in _collapsed)
        {
            header.Opacity = 1;
        }

        _shifted.Clear();
        _collapsed.Clear();
        _applied = null;
        _placeholderRect = default;
    }

    /// <summary>
    /// Lays the receiving band out as «the others plus the placeholder»: the surviving
    /// headers and the placeholder slot take consecutive positions from the natural start of
    /// the header run, each header shifted into its position; the dragged header collapses.
    /// The gap right after the dragged header's own position lays out identically to the
    /// natural order — the identity drop previews the tab staying in place (DA-E40).
    /// </summary>
    private Rect LayOutReceiver(StripReorderPreview preview, double placeholderWidth)
    {
        var headers = preview.Receiver.Headers;
        var others = new List<StripHeaderView>(headers.Length);
        StripHeaderView? dragged = null;
        foreach (var header in headers)
        {
            if (string.Equals(header.Header.TabId, preview.DraggedId, StringComparison.Ordinal))
            {
                dragged = header;
            }
            else
            {
                others.Add(header);
            }
        }

        int insertAt;
        if (preview.PredecessorId is null)
        {
            insertAt = 0;
        }
        else if (string.Equals(preview.PredecessorId, preview.DraggedId, StringComparison.Ordinal))
        {
            // The gap right after itself: the placeholder takes the dragged header's own position.
            insertAt = dragged is { } own ? others.Count(o => o.Rect.X < own.Rect.X) : others.Count;
        }
        else
        {
            var at = others.FindIndex(o => string.Equals(
                o.Header.TabId, preview.PredecessorId, StringComparison.Ordinal));
            // A predecessor gone from the strip falls back to the end, like the commit does.
            insertAt = at < 0 ? others.Count : at + 1;
        }

        var template = headers[0].Rect;
        var x = template.X;
        var placeholder = default(Rect);
        for (var i = 0; i <= others.Count; i++)
        {
            if (i == insertAt)
            {
                placeholder = new Rect(x, template.Y, placeholderWidth, template.Height);
                x += placeholderWidth;
            }

            if (i == others.Count)
            {
                break;
            }

            Shift(others[i], x - others[i].Rect.X);
            x += others[i].Rect.Width;
        }

        if (dragged is { } view)
        {
            Collapse(view.Header);
        }

        return placeholder.Intersect(preview.Receiver.Rect);
    }

    /// <summary>The donor-side collapse of a cross-strip hover: the surviving headers close the dragged header's gap.</summary>
    private void CollapseRun(StripBandView band, string draggedId)
    {
        if (band.Headers.IsEmpty)
        {
            return;
        }

        var x = band.Headers[0].Rect.X;
        foreach (var header in band.Headers)
        {
            if (string.Equals(header.Header.TabId, draggedId, StringComparison.Ordinal))
            {
                Collapse(header.Header);
                continue;
            }

            Shift(header, x - header.Rect.X);
            x += header.Rect.Width;
        }
    }

    private void Shift(StripHeaderView view, double offset)
    {
        if (Math.Abs(offset) < 0.01)
        {
            return; // an unshifted header keeps the projection's own (null) transform
        }

        view.Header.RenderTransform = new TranslateTransform(offset, 0);
        _shifted.Add(view.Header);
    }

    private void Collapse(DockTabHeader header)
    {
        header.Opacity = 0;
        _collapsed.Add(header);
    }
}
