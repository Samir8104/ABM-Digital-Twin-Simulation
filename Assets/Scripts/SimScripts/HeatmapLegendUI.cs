using System;
using System.Globalization;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Built from the simulation's existing font and button sprites; lives on the
// canvas so its position follows the same device-independent scaling as the HUD.
public sealed class HeatmapLegendUI : MonoBehaviour
{
    private SimulationHeatmap map;
    private GameObject panel;
    private TMP_FontAsset font;
    private Sprite buttonSprite;
    private ColorBlock buttonColors;
    private TMP_Text units, details, status;
    private readonly TMP_Text[] ticks = new TMP_Text[5];
    private TMP_InputField maximum;
    private Texture2D ramp;
    private float nextRefresh;

    public void Initialize(SimulationHeatmap owner, TimeManager clock)
    {
        map = owner;
        var canvas = clock != null && clock.timeText != null ? clock.timeText.canvas : FindFirstObjectByType<Canvas>();
        if (canvas == null) return;
        font = clock != null && clock.timeText != null ? clock.timeText.font : TMP_Settings.defaultFontAsset;
        var speed = FindFirstObjectByType<SimSpeedUI>();
        if (speed != null && speed.speedButtons.Count > 0 && speed.speedButtons[0].button != null)
        {
            var source = speed.speedButtons[0].button;
            if (source.targetGraphic is Image image) buttonSprite = image.sprite;
            buttonColors = source.colors;
        }
        else buttonColors = ColorBlock.defaultColorBlock;
        panel = Rect("Heatmap legend", canvas.transform, 0, 0, 390, 350).gameObject;
        var rect = (RectTransform)panel.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1, 1);
        rect.anchoredPosition = new Vector2(-24, -24);
        var background = panel.AddComponent<Image>(); background.color = new Color(0.08f, 0.10f, 0.13f, 0.94f);
        background.sprite = buttonSprite; background.type = Image.Type.Sliced;
        Text("Heatmap", 16, 12, 358, 28, 22).fontStyle = FontStyles.Bold;
        units = Text("", 16, 46, 358, 44, 17);
        details = Text("", 16, 96, 358, 32, 14);
        ramp = new Texture2D(256, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var pixels = new Color[256];
        for (int i = 0; i < pixels.Length; i++) { pixels[i] = map.ScaleColor(i / 255f); pixels[i].a = 1; }
        ramp.SetPixels(pixels); ramp.Apply(false);
        Rect("Numerical colour scale", panel.transform, 16, -136, 358, 18).gameObject.AddComponent<RawImage>().texture = ramp;
        for (int i = 0; i < ticks.Length; i++)
        {
            ticks[i] = Text("", 16 + i * 75, 158, i == 4 ? 58 : 80, 24, 13);
            if (i == 4) ticks[i].alignment = TextAlignmentOptions.TopRight;
        }
        Button("Change measure", 16, 198, 160, () => {
            map.SetMeasure((HeatmapMeasure)(((int)map.measure + 1) % Enum.GetValues(typeof(HeatmapMeasure)).Length));
            Refresh();
        });
        Text("Red from", 194, 204, 80, 26, 15);
        var input = Rect("Red threshold", panel.transform, 276, -198, 98, 30);
        var imageInput = input.gameObject.AddComponent<Image>(); imageInput.color = Color.white; imageInput.sprite = buttonSprite; imageInput.type = Image.Type.Sliced;
        maximum = input.gameObject.AddComponent<TMP_InputField>();
        var inputText = Rect("Value", input, 6, -3, 86, 24).gameObject.AddComponent<TextMeshProUGUI>();
        inputText.font = font; inputText.fontSize = 17; inputText.color = Color.black; inputText.raycastTarget = false;
        maximum.textViewport = input; maximum.textComponent = inputText; maximum.targetGraphic = imageInput;
        maximum.contentType = TMP_InputField.ContentType.DecimalNumber;
        maximum.onEndEdit.AddListener(value => {
            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)) map.SetScale(parsed);
            Refresh();
        });
        Button("Export files", 16, 242, 172, () => {
            try { map.ExportSnapshot(); status.text = "Saved TIFFs, data, and the offline viewer."; }
            catch (Exception e) { status.text = "Export failed. See the Console for details."; Debug.LogException(e); }
        });
        Button("Open saved view", 202, 242, 172, () => {
            try { string path = Path.Combine(map.ExportSnapshot(false), "heatmap-viewer.html"); Application.OpenURL(new Uri(path).AbsoluteUri); }
            catch (Exception e) { status.text = "Could not open the saved view."; Debug.LogException(e); }
        });
        status = Text("Linear scale shared by all floors. Gray means no observation yet. Counts are simulation estimates.", 16, 286, 358, 56, 13);
        Refresh();
    }

    private RectTransform Rect(string name, Transform parent, float x, float y, float width, float height)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.gameObject.layer = parent.gameObject.layer; rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = new Vector2(x, y); rect.sizeDelta = new Vector2(width, height);
        return rect;
    }
    private TMP_Text Text(string value, float x, float top, float width, float height, float size)
    {
        var text = Rect(value.Length > 0 ? value : "Label", panel.transform, x, -top, width, height).gameObject.AddComponent<TextMeshProUGUI>();
        text.font = font; text.fontSize = size; text.color = Color.white; text.text = value;
        text.alignment = TextAlignmentOptions.TopLeft; text.raycastTarget = false;
        return text;
    }
    private void Button(string label, float x, float top, float width, Action action)
    {
        var rect = Rect(label, panel.transform, x, -top, width, 32);
        var image = rect.gameObject.AddComponent<Image>(); image.sprite = buttonSprite; image.type = Image.Type.Sliced; image.color = Color.white;
        var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = image; button.colors = buttonColors;
        button.onClick.AddListener(() => action());
        var text = Rect("Label", rect, 0, 0, width, 32).gameObject.AddComponent<TextMeshProUGUI>();
        text.font = font; text.fontSize = 17; text.color = Color.black; text.text = label;
        text.alignment = TextAlignmentOptions.Center; text.raycastTarget = false;
    }
    public void SetVisible(bool value) { if (panel != null) panel.SetActive(value); }
    private void LateUpdate()
    {
        if (panel == null || !panel.activeSelf || map == null || Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + 0.25f; Refresh();
    }
    private void Refresh()
    {
        if (units == null || map.Grid == null) return;
        units.text = HeatmapMeasures.Label(map.measure);
        double hours = map.Grid.SimulatedSeconds / 3600d;
        details.text = hours.ToString("0.###", CultureInfo.InvariantCulture) + " simulated hours | " + map.Grid.CellSize.ToString("0.##", CultureInfo.InvariantCulture) + " m cells\n" +
            (map.FloorView != null && map.FloorView.Active ? "Floor " + (map.FloorView.SelectedFloor + 1) : "All floors") + " | Fixed linear scale";
        for (int i = 0; i < ticks.Length; i++) ticks[i].text = (i == 4 ? ">=" : "") + (map.scaleMaximum * i / 4d).ToString("G3", CultureInfo.InvariantCulture);
        if (maximum != null && !maximum.isFocused) maximum.SetTextWithoutNotify(map.scaleMaximum.ToString("G6", CultureInfo.InvariantCulture));
    }
    private void OnDestroy() { if (panel != null) Destroy(panel); if (ramp != null) Destroy(ramp); }
}
