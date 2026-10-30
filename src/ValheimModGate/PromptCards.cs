using System.Linq;
using UnityEngine;

namespace ModGate
{
    internal sealed partial class Prompt
    {
        private const float ProfileCardGap = 14, ProfileCardPadding = 20;

        private void DrawProfileCards()
        {
            var width = Mathf.Max(1, contentWidth - 20);
            var wideOffline = width >= 660;
            var offlineHeight = wideOffline ? 112 : width - ProfileCardPadding * 2 >= 280 ? 166 : 210;
            DrawOfflineCard(Reserve(offlineHeight), wideOffline);
            GUILayout.Space(ProfileCardGap);

            var columns = contentWidth >= 700 ? 2 : 1;
            var cardWidth = (width - ProfileCardGap * (columns - 1)) / columns;
            for (var first = 1; first <= 4; first += columns)
            {
                var rowHeight = 150f;
                for (var column = 0; column < columns && first + column <= 4; column++)
                {
                    var slot = "server-" + (first + column);
                    if (catalog.Servers.Any(p => p.Slot == slot))
                        rowHeight = Mathf.Max(rowHeight, SavedProfileCardHeight(cardWidth, moreProfile == slot));
                }
                var row = Reserve(rowHeight);
                for (var column = 0; column < columns && first + column <= 4; column++)
                {
                    var number = first + column;
                    var slot = "server-" + number;
                    var saved = catalog.Servers.FirstOrDefault(p => p.Slot == slot);
                    var card = new Rect(row.x + column * (cardWidth + ProfileCardGap), row.y, cardWidth, row.height);
                    if (saved == null) DrawEmptyProfileCard(card, number);
                    else DrawSavedProfileCard(card, saved, number);
                }
                GUILayout.Space(ProfileCardGap);
            }
            LabelAt(Reserve(28), "Server requirements are checked when you join.", "small", "muted");
        }

        private static float SavedProfileCardHeight(float width, bool expanded)
            => (width - ProfileCardPadding * 2 >= 280 ? 202 : 246) + (expanded ? 52 : 0);

        private void DrawOfflineCard(Rect area, bool wide)
        {
            Card(area);
            var left = area.x + ProfileCardPadding;
            var inner = area.width - ProfileCardPadding * 2;
            var state = catalog.Active == "offline" ? "Active" : "Ready";
            var count = catalog.OfflineMods.Count(m => m.Guid != Constants.Guid);
            if (wide)
            {
                var actionsX = area.x + area.width - ProfileCardPadding - 308;
                var badgeX = actionsX - 80;
                LabelAt(new Rect(left, area.y + 18, badgeX - left - 16, 36), "Offline", "title");
                LabelAt(new Rect(left, area.y + 58, badgeX - left - 16, 32), ProfileModCount(count) + " · Your personal setup", "body", "muted");
                Badge(new Rect(badgeX, area.y + 42, 68, 28), state, "green");
                DrawProfileCardActions(new Rect(actionsX, area.y + 38, 308, 36), "offline");
            }
            else
            {
                LabelAt(new Rect(left, area.y + 18, inner - 84, 36), "Offline", "title");
                Badge(new Rect(left + inner - 68, area.y + 23, 68, 28), state, "green");
                LabelAt(new Rect(left, area.y + 60, inner, 32), ProfileModCount(count) + " · Your personal setup", "body", "muted");
                DrawProfileCardActions(new Rect(left, area.y + 110, inner, area.height - 130), "offline");
            }
        }

        private void DrawSavedProfileCard(Rect area, SavedProfile saved, int number)
        {
            Card(area);
            var slot = saved.Slot;
            var expanded = moreProfile == slot;
            var left = area.x + ProfileCardPadding;
            var inner = area.width - ProfileCardPadding * 2;
            var state = State(saved);
            var tone = saved.Incomplete ? "red" : state == "Needs update" ? "yellow" : "green";
            var badgeWidth = Mathf.Clamp(state.Length * 6.5f + 18, 58, 146);
            var moreX = left + inner - 64;
            var badgeX = inner < 290 ? left : moreX - badgeWidth - 8;
            if (inner >= 290) LabelAt(new Rect(left, area.y + 21, Mathf.Max(1, badgeX - left - 8), 24), "SERVER " + number, "small", "muted");
            Badge(new Rect(badgeX, area.y + 18, badgeWidth, 28), state, tone);

            var wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && !Busy;
            try
            {
                if (Action(new Rect(moreX, area.y + 16, 64, 32), "More (" + slot + ")", "quiet"))
                    moreProfile = expanded ? "" : slot;
            }
            finally { GUI.enabled = wasEnabled; }

            LabelAt(new Rect(left, area.y + 55, inner, 46), string.IsNullOrWhiteSpace(saved.Name) ? "Server profile " + number : saved.Name, "heading");
            var description = saved.Incomplete ? "Setup incomplete" : state == "Needs update" ? "Review server changes" : catalog.Active == slot ? "Currently active" : "Ready to join";
            LabelAt(new Rect(left, area.y + 108, inner, 32), ProfileModCount(saved.Mods.Count(m => m.Guid != Constants.Guid)) + " · " + description, "body", "muted");
            var baseHeight = SavedProfileCardHeight(area.width, false);
            DrawProfileCardActions(new Rect(left, area.y + 146, inner, baseHeight - 166), slot);
            if (!expanded) return;

            Divider(new Rect(left, area.y + baseHeight - 4, inner, 1));
            GUI.enabled = wasEnabled && !Busy;
            try
            {
                if (Action(new Rect(left, area.y + baseHeight + 8, inner, 32), "Delete " + slot, "quiet"))
                { moreProfile = ""; deleteSlot = slot; }
            }
            finally { GUI.enabled = wasEnabled; }
        }

        private void DrawProfileCardActions(Rect area, string slot)
        {
            var wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && !Busy;
            try
            {
                var stacked = area.width < 280;
                var openWidth = stacked ? area.width : Mathf.Min(128, (area.width - 8) * .43f);
                if (Action(new Rect(area.x, area.y, openWidth, 36), "Open profile (" + slot + ")"))
                { moreProfile = ""; navigationTab = "Mods"; OpenEditor(slot); }
                var activate = stacked ? new Rect(area.x, area.y + 44, area.width, 36) : new Rect(area.x + openWidth + 8, area.y, area.width - openWidth - 8, 36);
                if (Action(activate, "Activate & restart (" + slot + ")", "primary"))
                {
                    ScheduleCommand(new ProfileCommand { Kind = "switch", Target = slot });
                    if (scheduled) Restart();
                }
            }
            finally { GUI.enabled = wasEnabled; }
        }

        private void DrawEmptyProfileCard(Rect area, int number)
        {
            Card(area, true);
            var left = area.x + ProfileCardPadding;
            var inner = area.width - ProfileCardPadding * 2;
            var top = area.y + (area.height - 104) / 2;
            LabelAt(new Rect(left, top, 26, 36), "+", "title", "muted");
            LabelAt(new Rect(left + 36, top + 4, Mathf.Max(1, inner - 36), 32), "Server slot " + number, "heading", "muted");
            LabelAt(new Rect(left, top + 51, inner, 48), "Join a server to set up this profile.", "body", "muted");
        }

        private static string ProfileModCount(int count) => count + (count == 1 ? " mod" : " mods");
    }
}
