using UnityEngine;

namespace MWCSpectatorSync.UI
{
    public struct OverlayState
    {
        public bool Visible;
        public string Title;
        public string Mode;
        public string Transport;
        public string Status;
        public string LocalSteamId;
        public string RemoteSteamId;
        public string LevelName;
        public string ProgressMarker;
        public string Warning;
        public string Hint;
        public bool IsConnected;
        public int SendHz;
        public int ServerSendHz;
    }

    public sealed class Overlay
    {
        private GUIStyle _boxStyle;
        private GUIStyle _labelStyle;

        public void Draw(OverlayState state)
        {
            if (!state.Visible)
            {
                return;
            }

            EnsureStyles();
            GUILayout.BeginArea(new Rect(10f, 10f, 480f, 240f));
            GUILayout.BeginVertical(_boxStyle);

            GUILayout.Label(state.Title ?? "MWC Spectator Sync", _labelStyle);
            GUILayout.Label("Mode: " + state.Mode + "  Transport: " + state.Transport, _labelStyle);
            GUILayout.Label("Status: " + state.Status, _labelStyle);

            if (!string.IsNullOrEmpty(state.LocalSteamId))
            {
                GUILayout.Label("Local SteamID64: " + state.LocalSteamId, _labelStyle);
            }
            if (!string.IsNullOrEmpty(state.RemoteSteamId))
            {
                GUILayout.Label("Remote SteamID64: " + state.RemoteSteamId, _labelStyle);
            }
            if (!string.IsNullOrEmpty(state.LevelName))
            {
                GUILayout.Label("Level: " + state.LevelName, _labelStyle);
            }
            if (!string.IsNullOrEmpty(state.ProgressMarker))
            {
                GUILayout.Label("Marker: " + state.ProgressMarker, _labelStyle);
            }

            if (state.SendHz > 0)
            {
                string hzText = state.SendHz.ToString();
                if (state.ServerSendHz > 0)
                {
                    hzText += " (host: " + state.ServerSendHz + ")";
                }
                GUILayout.Label("SendHz: " + hzText, _labelStyle);
            }

            if (!string.IsNullOrEmpty(state.Warning))
            {
                GUILayout.Label("Warning: " + state.Warning, _labelStyle);
            }

            if (!string.IsNullOrEmpty(state.Hint))
            {
                GUILayout.Label(state.Hint, _labelStyle);
            }

            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        private void EnsureStyles()
        {
            if (_boxStyle != null)
            {
                return;
            }
            _boxStyle = new GUIStyle(GUI.skin.box);
            _boxStyle.padding = new RectOffset(8, 8, 8, 8);
            _boxStyle.alignment = TextAnchor.UpperLeft;

            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.fontSize = 12;
            _labelStyle.normal.textColor = Color.white;
        }
    }
}
