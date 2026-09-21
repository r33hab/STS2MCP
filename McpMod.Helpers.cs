using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace STS2_MCP;

public static partial class McpMod
{
    // STS2 v0.107 removed CombatManager.IsPlayPhase. The turn phase now lives per-player on
    // PlayerCombatState.Phase, so these helpers rebuild the old semantics: we're in the play
    // phase when combat is running, it's the player side's turn, and the player is in Play.
    internal static bool IsPlayPhase(MegaCrit.Sts2.Core.Entities.Players.Player? player)
    {
        if (!IsPlayerSideTurn()) return false;
        return player?.PlayerCombatState?.Phase == MegaCrit.Sts2.Core.Combat.PlayerTurnPhase.Play;
    }

    // State-report variant: true when any player is still in the Play phase. In singleplayer
    // this is the local player; in co-op it means at least one player can still act.
    internal static bool IsPlayPhase(MegaCrit.Sts2.Core.Combat.ICombatState? combatState)
    {
        if (!IsPlayerSideTurn() || combatState == null) return false;
        foreach (var player in combatState.Players)
        {
            if (player?.PlayerCombatState?.Phase == MegaCrit.Sts2.Core.Combat.PlayerTurnPhase.Play)
                return true;
        }
        return false;
    }

    private static bool IsPlayerSideTurn()
    {
        var manager = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
        if (manager is not { IsInProgress: true }) return false;
        return manager.DebugOnlyGetState()?.CurrentSide == MegaCrit.Sts2.Core.Combat.CombatSide.Player;
    }

    private static string? SafeGetCardDescription(CardModel card, PileType pile = PileType.Hand)
    {
        try { return StripRichTextTags(card.GetDescriptionForPile(pile)).Replace("\n", " "); }
        catch { return SafeGetText(() => card.Description)?.Replace("\n", " "); }
    }

    private static CardModel? SafeBuildUpgradedCardPreview(CardModel card)
    {
        if (!card.IsUpgradable)
            return null;

        try
        {
            var preview = card.IsMutable
                ? (CardModel)card.MutableClone()
                : card.ToMutable();
            preview.UpgradeInternal();
            return preview;
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeGetCardUpgradePreviewDescription(CardModel card, PileType pile = PileType.Hand)
    {
        var preview = SafeBuildUpgradedCardPreview(card);
        if (preview != null)
            return SafeGetCardDescription(preview, pile);

        return card.IsUpgradable
            ? SafeGetText(() => card.GetDescriptionForUpgradePreview())?.Replace("\n", " ")
            : null;
    }

    internal static string? SafeGetText(Func<object?> getter)
    {
        try
        {
            var result = getter();
            if (result == null) return null;
            // If it's a LocString, call GetFormattedText
            if (result is MegaCrit.Sts2.Core.Localization.LocString locString)
                return StripRichTextTags(locString.GetFormattedText());
            return result.ToString();
        }
        catch { return null; }
    }

    internal static string StripRichTextTags(string text)
    {
        // Remove BBCode-style tags like [color=red], [/color], etc.
        // Special case: [img]res://path/to/file.png[/img] → [file.png]
        var sb = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '[')
            {
                // Check for [img]...[/img] pattern
                if (text.AsSpan(i).StartsWith("[img]"))
                {
                    int contentStart = i + 5; // length of "[img]"
                    int closeTag = text.IndexOf("[/img]", contentStart, StringComparison.Ordinal);
                    if (closeTag >= 0)
                    {
                        string path = text[contentStart..closeTag];
                        int lastSlash = path.LastIndexOf('/');
                        string filename = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
                        sb.Append('[').Append(filename).Append(']');
                        i = closeTag + 6; // length of "[/img]"
                        continue;
                    }
                }

                int end = text.IndexOf(']', i);
                if (end >= 0) { i = end + 1; continue; }
            }
            sb.Append(text[i]);
            i++;
        }
        return sb.ToString();
    }

    // The preference is passed down from the request rather than kept in a thread-static or
    // global flag: requests are served concurrently on ThreadPool threads.
    internal static bool WantsPretty(HttpListenerRequest request)
    {
        var query = request.QueryString;
        string? value = query["pretty"];
        if (value == null)
        {
            // "?pretty" without "=" has no name, so NameValueCollection files it under the
            // null key, joining several such parameters with commas.
            string? valueless = query[null];
            if (valueless == null)
                return false;
            foreach (string part in valueless.Split(','))
            {
                if (part.Trim().Equals("pretty", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        return value.Length == 0
            || value.Equals("1", StringComparison.Ordinal)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    internal static void SendJson(HttpListenerResponse response, object data, bool pretty = false)
    {
        string json = JsonSerializer.Serialize(data, pretty ? _jsonOptionsPretty : _jsonOptions);
        byte[] buffer = Encoding.UTF8.GetBytes(json);
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.Close();
    }

    internal static void SendText(HttpListenerResponse response, string text, string contentType = "text/plain")
    {
        byte[] buffer = Encoding.UTF8.GetBytes(text);
        response.ContentType = $"{contentType}; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.Close();
    }

    internal static void SendError(
        HttpListenerResponse response, int statusCode, string message, bool pretty = false)
    {
        response.StatusCode = statusCode;
        // "status" is carried here too so a client never has to branch on the HTTP code:
        // transport-level failures (bad JSON, unknown route, wrong method) read exactly
        // like the action-level failures produced by Error().
        SendJson(response, new Dictionary<string, object?>
        {
            ["status"] = "error",
            ["error"] = message
        }, pretty);
    }

    private static Dictionary<string, object?> Error(string message)
    {
        return new Dictionary<string, object?> { ["status"] = "error", ["error"] = message };
    }

    /// <summary>
    /// Last line of defence for the POST contract: every action result must carry
    /// "status". A future action that forgets it would silently break clients that
    /// branch on that key alone, so the value is derived here from the presence of
    /// "error" rather than left missing.
    /// </summary>
    private static Dictionary<string, object?> EnsureStatus(Dictionary<string, object?> result)
    {
        if (!result.ContainsKey("status"))
            result["status"] = result.ContainsKey("error") ? "error" : "ok";
        return result;
    }

    private static object? GetInstanceFieldValue(object source, string fieldName)
    {
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.DeclaredOnly;

        for (var type = source.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(fieldName, Flags);
            if (field != null)
                return field.GetValue(source);
        }

        return null;
    }

    private static void AddMenuOptionIfVisible(
        List<Dictionary<string, object?>> options,
        object owner,
        string fieldName,
        string label)
    {
        try
        {
            var btn = GetInstanceFieldValue(owner, fieldName);
            if (btn is Control ctrl && IsNodeVisible(ctrl))
            {
                var isEnabled = btn.GetType().GetProperty("IsEnabled")?.GetValue(btn) as bool?;
                options.Add(new Dictionary<string, object?>
                {
                    ["name"] = label,
                    ["enabled"] = isEnabled ?? true
                });
            }
        }
        catch { }
    }

    internal static List<T> FindAll<T>(Node start) where T : Node
    {
        var list = new List<T>();
        if (IsLiveNode(start))
            FindAllRecursive(start, list);
        return list;
    }

    /// <summary>
    /// FindAll variant that sorts results by visual position (row-major: top-to-bottom, left-to-right).
    /// NGridCardHolder.OnFocus() calls MoveToFront() which scrambles child order for z-rendering.
    /// Sorting by GlobalPosition restores the correct visual order for both single-row (card rewards,
    /// choose-a-card) and multi-row (deck selection grids) layouts.
    /// </summary>
    internal static List<T> FindAllSortedByPosition<T>(Node start) where T : Control
    {
        var list = FindAll<T>(start);
        list.Sort((a, b) =>
        {
            int cmp = a.GlobalPosition.Y.CompareTo(b.GlobalPosition.Y);
            return cmp != 0 ? cmp : a.GlobalPosition.X.CompareTo(b.GlobalPosition.X);
        });
        return list;
    }

    private static void FindAllRecursive<T>(Node node, List<T> found) where T : Node
    {
        if (!IsLiveNode(node))
            return;
        if (node is T item)
            found.Add(item);
        try
        {
            foreach (var child in node.GetChildren())
                FindAllRecursive(child, found);
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Last-resort description for a hover tip whose own text is empty: ask the model
    /// the tip points at. PowerModel/RelicModel/CardModel each expose their own text
    /// under a different name, so try them in order of specificity.
    /// </summary>
    private static string? DescribeCanonicalModel(IHoverTip tip)
    {
        object? model;
        try { model = tip.CanonicalModel; }
        catch { return null; }
        if (model == null) return null;

        foreach (var propertyName in new[] { "SmartDescription", "DynamicDescription", "Description" })
        {
            try
            {
                var property = model.GetType().GetProperty(propertyName);
                if (property == null) continue;
                var value = property.GetValue(model);
                if (value == null) continue;
                var raw = value.ToString();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var text = StripRichTextTags(raw);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
            catch { /* try the next one */ }
        }

        return null;
    }

    private static List<Dictionary<string, object?>> BuildHoverTips(IEnumerable<IHoverTip> tips)
    {
        var result = new List<Dictionary<string, object?>>();
        try
        {
            var seen = new HashSet<string>();
            foreach (var tip in IHoverTip.RemoveDupes(tips))
            {
                try
                {
                    string? title = null;
                    string? description = null;

                    if (tip is HoverTip ht)
                    {
                        title = ht.Title != null ? StripRichTextTags(ht.Title) : null;
                        description = StripRichTextTags(ht.Description);
                    }
                    else if (tip is CardHoverTip cardTip)
                    {
                        title = SafeGetText(() => cardTip.Card.Title);
                        description = SafeGetCardDescription(cardTip.Card);
                    }

                    // Some keywords ship with no description text at all (Sly is one),
                    // and some describe themselves in terms of themselves. Fall back to
                    // the canonical model the tip points at, which carries the real
                    // effect text, before giving up.
                    if (string.IsNullOrWhiteSpace(description))
                        description = DescribeCanonicalModel(tip);

                    if (title == null && description == null) continue;

                    string key = title ?? description!;
                    if (!seen.Add(key)) continue;

                    var entry = new Dictionary<string, object?>
                    {
                        ["name"] = title,
                        ["description"] = description
                    };

                    string? tipId = SafeGetText(() => tip.Id);
                    if (!string.IsNullOrWhiteSpace(tipId))
                        entry["id"] = tipId;
                    if (string.IsNullOrWhiteSpace(description))
                        entry["description_missing"] = true;

                    result.Add(entry);
                }
                catch { /* skip individual tip on error */ }
            }
        }
        catch { /* return partial results */ }
        return result;
    }

    internal static T? FindFirst<T>(Node start) where T : Node
    {
        if (!IsLiveNode(start))
            return null;
        if (start is T result)
            return result;
        Godot.Collections.Array<Node> children;
        try
        {
            children = start.GetChildren();
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        foreach (var child in children)
        {
            var val = FindFirst<T>(child);
            if (val != null) return val;
        }
        return null;
    }

    internal static bool IsLiveNode(Node? node)
    {
        try
        {
            return node != null && GodotObject.IsInstanceValid(node) && !node.IsQueuedForDeletion();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    internal static bool IsNodeVisible(CanvasItem? node)
    {
        try
        {
            return node != null && IsLiveNode(node) && node.Visible && node.IsVisibleInTree();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool IsMapScreenOpenOrVisible()
    {
        var mapScreen = NMapScreen.Instance;
        return mapScreen != null && (mapScreen.IsOpen || IsNodeVisible(mapScreen));
    }

    private static bool IsControlVisibleInTree(NClickableControl? control)
    {
        try
        {
            return control != null &&
                   IsLiveNode(control) &&
                   IsNodeVisible(control);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static Node? GetOpenModalNode()
    {
        try
        {
            return NModalContainer.Instance?.OpenModal as Node;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private static bool IsDescendantOf(Node? node, Node ancestor)
    {
        try
        {
            for (var current = node; current != null && IsLiveNode(current); current = current.GetParent())
            {
                if (ReferenceEquals(current, ancestor))
                    return true;
            }
        }
        catch (ObjectDisposedException) { }

        return false;
    }

    private static bool IsControlVisibleInOpenModal(NClickableControl? control, Node? openModal = null)
    {
        openModal ??= GetOpenModalNode();
        if (control == null || openModal == null || !IsLiveNode(control) || !IsLiveNode(openModal))
            return false;

        try
        {
            return control.Visible && IsDescendantOf(control, openModal);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool IsPopupButtonActionable(NClickableControl? control)
    {
        if (control == null || !IsLiveNode(control))
            return false;

        try
        {
            return control.IsEnabled &&
                   (IsControlVisibleInTree(control) || IsControlVisibleInOpenModal(control));
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool IsControlVisibleOrActionable(NClickableControl? control)
    {
        return IsControlVisibleInTree(control) && control!.IsEnabled;
    }

    private static bool IsFtueNodeActive(Node node)
    {
        if (node is not CanvasItem canvas || !IsLiveNode(node))
            return false;

        if (IsNodeVisible(canvas))
            return true;

        if (ReferenceEquals(GetOpenModalNode(), node))
            return true;

        try
        {
            return GetInstanceFieldValue(node, "_confirmButton") is NClickableControl confirmButton &&
                   (IsControlVisibleInTree(confirmButton) || IsControlVisibleInOpenModal(confirmButton));
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static Dictionary<string, object?>? BuildVisibleFtueState(Node root)
    {
        var tutorialFtue = FindVisibleAcceptTutorialsFtue(root);
        if (tutorialFtue != null && IsFtueNodeActive(tutorialFtue))
        {
            var state = NewStateResult("menu");
            state["menu_screen"] = "tutorial_prompt";
            state["message"] = "Enable Tutorials? Choose yes or no.";
            state["options"] = new List<Dictionary<string, object?>>
            {
                new() { ["name"] = "no", ["enabled"] = true },
                new() { ["name"] = "yes", ["enabled"] = true }
            };
            return state;
        }

        var ftue = FindVisibleGenericFtue(root);
        if (ftue != null)
        {
            var canAdvance = FindFtueAdvanceButton(ftue) != null;
            var state = NewStateResult("menu");
            state["menu_screen"] = "tutorial";
            state["message"] = "Tutorial popup active. Use advance to dismiss.";
            state["options"] = new List<Dictionary<string, object?>>
            {
                new() { ["name"] = "advance", ["enabled"] = canAdvance },
                new() { ["name"] = "proceed", ["enabled"] = canAdvance }
            };
            return state;
        }

        var popup = BuildVisiblePopupState(root);
        if (popup != null)
            return popup;

        return null;
    }

    private static Dictionary<string, object?>? BuildVisiblePopupState(Node root)
    {
        var popup = FindVisibleVerticalPopup(root);
        var options = popup != null
            ? GetPopupOptions(popup)
            : GetVisiblePopupButtonOptions(root);

        var stateOptions = new List<Dictionary<string, object?>>();
        foreach (var option in options)
        {
            stateOptions.Add(new Dictionary<string, object?>
            {
                ["name"] = option.Name,
                ["enabled"] = option.Button.IsEnabled
            });
        }

        if (stateOptions.Count == 0)
            return null;

        var state = NewStateResult("menu");
        state["menu_screen"] = "popup";
        state["message"] = popup != null ? GetVerticalPopupText(popup, "TitleLabel") ?? "Popup active." : "Popup active.";
        state["body"] = popup != null ? GetVerticalPopupText(popup, "BodyLabel") : null;
        state["options"] = stateOptions;
        return state;
    }

    private static MegaCrit.Sts2.Core.Nodes.Ftue.NAcceptTutorialsFtue? FindVisibleAcceptTutorialsFtue(Node root)
    {
        var openModal = GetOpenModalNode();
        if (openModal is MegaCrit.Sts2.Core.Nodes.Ftue.NAcceptTutorialsFtue openFtue &&
            IsFtueNodeActive(openFtue))
        {
            return openFtue;
        }

        foreach (var ftue in FindAll<MegaCrit.Sts2.Core.Nodes.Ftue.NAcceptTutorialsFtue>(root))
        {
            if (IsFtueNodeActive(ftue) || ReferenceEquals(openModal, ftue))
                return ftue;
        }
        return null;
    }

    private static bool IsAnyFtueVisible(Node root)
    {
        return BuildVisibleFtueState(root) != null;
    }

    private static NVerticalPopup? FindVisibleVerticalPopup(Node root)
    {
        var openModal = GetOpenModalNode();
        if (openModal is NVerticalPopup openPopup && GetPopupOptions(openPopup).Count > 0)
            return openPopup;

        if (openModal != null)
        {
            foreach (var popup in FindAll<NVerticalPopup>(openModal))
            {
                if (GetPopupOptions(popup).Count > 0)
                    return popup;
            }
        }

        foreach (var popup in FindAll<NVerticalPopup>(root))
        {
            if ((IsNodeVisible(popup) || (openModal != null && IsDescendantOf(popup, openModal))) &&
                GetPopupOptions(popup).Count > 0)
            {
                return popup;
            }
        }
        return null;
    }

    private static List<(string Name, NClickableControl Button)> GetPopupOptions(NVerticalPopup popup)
    {
        var options = new List<(string Name, NClickableControl Button)>();
        AddPopupOption(options, popup.YesButton, "yes");
        AddPopupOption(options, popup.NoButton, "no");
        return options;
    }

    private static List<(string Name, NClickableControl Button)> GetVisiblePopupButtonOptions(Node root)
    {
        var options = new List<(string Name, NClickableControl Button)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var openModal = GetOpenModalNode();
        if (openModal != null)
        {
            foreach (var button in FindAll<NClickableControl>(openModal))
                AddVisiblePopupButtonOption(options, seen, button, null, openModal);
        }

        foreach (var button in FindAll<NPopupYesNoButton>(root))
        {
            AddVisiblePopupButtonOption(options, seen, button, button.IsYes ? "yes" : "no", null);
        }

        foreach (var modal in FindAll<NModalContainer>(root))
        {
            if (!IsNodeVisible(modal))
                continue;

            foreach (var button in FindAll<NClickableControl>(modal))
                AddVisiblePopupButtonOption(options, seen, button, null, null);
        }

        return options;
    }

    private static void AddVisiblePopupButtonOption(
        List<(string Name, NClickableControl Button)> options,
        HashSet<string> seen,
        NClickableControl? button,
        string? fallback,
        Node? openModal)
    {
        if (!(IsControlVisibleInTree(button) || IsControlVisibleInOpenModal(button, openModal)))
            return;

        var label = button is NPopupYesNoButton popupButton
            ? GetPopupButtonLabel(popupButton)
            : GetClickableControlLabel(button!);
        var name = NormalizeMenuOptionName(label) ?? fallback;
        if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
            return;

        options.Add((name, button!));
    }

    private static void AddPopupOption(
        List<(string Name, NClickableControl Button)> options,
        NPopupYesNoButton? button,
        string fallback)
    {
        if (!IsPopupButtonActionable(button))
            return;

        var label = GetPopupButtonLabel(button!);
        var name = NormalizeMenuOptionName(label) ?? fallback;
        options.Add((name, button!));
    }

    private static string? GetVerticalPopupText(NVerticalPopup popup, string propertyName)
    {
        var label = popup.GetType().GetProperty(propertyName)?.GetValue(popup);
        return GetNodeText(label);
    }

    private static string? GetPopupButtonLabel(NPopupYesNoButton button)
    {
        var label = GetInstanceFieldValue(button, "_label");
        return GetNodeText(label);
    }

    private static string? GetClickableControlLabel(NClickableControl button)
    {
        foreach (var fieldName in new[] { "_label", "_textLabel", "_title", "_buttonLabel" })
        {
            var label = GetNodeText(GetInstanceFieldValue(button, fieldName));
            if (!string.IsNullOrWhiteSpace(label))
                return label;
        }

        foreach (var propName in new[] { "Text", "Label", "Title" })
        {
            var label = SafeGetText(() => button.GetType().GetProperty(propName)?.GetValue(button));
            if (!string.IsNullOrWhiteSpace(label))
                return label;
        }

        var childText = FindNodeTextRecursive(button);
        if (!string.IsNullOrWhiteSpace(childText))
            return childText;

        var nodeName = button.Name.ToString();
        if (!string.IsNullOrWhiteSpace(nodeName))
            return nodeName.EndsWith("Button", StringComparison.OrdinalIgnoreCase)
                ? nodeName[..^"Button".Length]
                : nodeName;

        return null;
    }

    private static string? FindNodeTextRecursive(Node start)
    {
        if (!IsLiveNode(start))
            return null;

        var text = GetNodeText(start);
        if (!string.IsNullOrWhiteSpace(text))
            return text;

        Godot.Collections.Array<Node> children;
        try
        {
            children = start.GetChildren();
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        foreach (var child in children)
        {
            text = FindNodeTextRecursive(child);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    private static string? GetNodeText(object? node)
    {
        if (node == null)
            return null;

        foreach (var propName in new[] { "Text", "BbcodeText" })
        {
            var text = SafeGetText(() => node.GetType().GetProperty(propName)?.GetValue(node));
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    private static string? NormalizeMenuOptionName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var sb = new StringBuilder();
        foreach (var ch in StripRichTextTags(text).Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '_')
                sb.Append('_');
        }

        var normalized = sb.ToString().Trim('_');
        return normalized.Length == 0 ? null : normalized;
    }

    private static Node? FindVisibleGenericFtue(Node start)
    {
        if (ReferenceEquals(start, (Godot.Engine.GetMainLoop() as SceneTree)?.Root))
        {
            var openModal = GetOpenModalNode();
            if (openModal != null)
            {
                var openFtue = FindVisibleGenericFtue(openModal);
                if (openFtue != null)
                    return openFtue;
            }
        }

        if (!IsLiveNode(start))
            return null;

        var typeName = start.GetType().FullName ?? "";
        if (typeName.StartsWith("MegaCrit.Sts2.Core.Nodes.Ftue.", StringComparison.Ordinal) &&
            !typeName.EndsWith(".NAcceptTutorialsFtue", StringComparison.Ordinal) &&
            IsFtueNodeActive(start))
        {
            return start;
        }

        Godot.Collections.Array<Node> children;
        try
        {
            children = start.GetChildren();
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        foreach (var child in children)
        {
            var val = FindVisibleGenericFtue(child);
            if (val != null)
                return val;
        }

        return null;
    }

    private static NClickableControl? FindFtueAdvanceButton(Node ftue)
    {
        foreach (var fieldName in new[]
        {
            "_confirmButton",
            "_advanceButton",
            "_nextButton",
            "_proceedButton",
            "_acknowledgeButton",
            "_arrowButton",
            "_rightArrowButton",
            "_rightButton"
        })
        {
            if (GetInstanceFieldValue(ftue, fieldName) is NClickableControl fieldButton &&
                IsPopupButtonActionable(fieldButton))
            {
                return fieldButton;
            }
        }

        try
        {
            foreach (var field in ftue.GetType().GetFields(
                         System.Reflection.BindingFlags.Public |
                         System.Reflection.BindingFlags.NonPublic |
                         System.Reflection.BindingFlags.Instance))
            {
                if (field.GetValue(ftue) is NClickableControl fieldButton &&
                    IsPopupButtonActionable(fieldButton))
                {
                    return fieldButton;
                }
            }
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        foreach (var button in FindAll<NClickableControl>(ftue))
        {
            if (IsPopupButtonActionable(button))
                return button;
        }

        return null;
    }
}
