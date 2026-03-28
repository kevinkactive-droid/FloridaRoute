using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace FloridaRouteApp
{
    public partial class MainWindow : Window
    {
        // ── API Keys & Config ─────────────────────────────────────────────
        private const string MapboxToken = "YOUR_MAPBOX_TOKEN_HERE";

        // ── Florida bounds ────────────────────────────────────────────────
        private const double MinLat = 24.396308, MaxLat = 31.000968;
        private const double MinLon = -87.634938, MaxLon = -79.974306;

        // ── Map styles (name → Mapbox style URL) ──────────────────────────
        private readonly Dictionary<string, string> _mapStyles = new()
        {
            { "Mapbox Dark",      "mapbox://styles/mapbox/dark-v11" },
            { "Mapbox Streets",   "mapbox://styles/mapbox/streets-v12" },
            { "Mapbox Satellite", "mapbox://styles/mapbox/satellite-streets-v12" },
            { "Mapbox Light",     "mapbox://styles/mapbox/light-v11" },
            { "Mapbox Outdoors",  "mapbox://styles/mapbox/outdoors-v12" },
        };

        private bool _mapReady = false;
        private static readonly HttpClient Http = new();

        public MainWindow()
        {
            InitializeComponent();
            InitMapStyleCombo();
            InitWebView();
        }

        // ── Map style combo ───────────────────────────────────────────────
        private void InitMapStyleCombo()
        {
            foreach (var key in _mapStyles.Keys)
                MapStyleCombo.Items.Add(key);
            MapStyleCombo.SelectedIndex = 0; // Mapbox Dark default
        }

        private void MapStyleCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!_mapReady) return;
            var styleName = MapStyleCombo.SelectedItem?.ToString() ?? "Mapbox Dark";
            var styleUrl  = _mapStyles[styleName];
            MapView.CoreWebView2.ExecuteScriptAsync(
                $"map.setStyle('{styleUrl}');");
            SetStatus($"Switched to {styleName}.");
        }

        // ── WebView2 init ─────────────────────────────────────────────────
        private async void InitWebView()
        {
            await MapView.EnsureCoreWebView2Async();
            MapView.CoreWebView2.NavigateToString(BuildMapHtml());
        }

        private void MapView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _mapReady = true;
            SetStatus("Map ready — enter two locations within Florida.");
        }

        // ── Key handler (Enter to calculate) ─────────────────────────────
        private void Entry_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) _ = CalculateRoute();
        }

        private void CalculateBtn_Click(object sender, RoutedEventArgs e)
        {
            _ = CalculateRoute();
        }

        // ── Main calculate flow ───────────────────────────────────────────
        private async Task CalculateRoute()
        {
            var originText = OriginBox.Text.Trim();
            var destText   = DestinationBox.Text.Trim();

            if (string.IsNullOrEmpty(originText) || string.IsNullOrEmpty(destText))
            {
                MessageBox.Show("Please enter both an origin and a destination.",
                                "Input Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetStatus("Looking up locations…", "#64748b");

            // Geocode both locations
            GeoPoint? origin, dest;
            try
            {
                origin = await Geocode(originText);
                dest   = await Geocode(destText);
            }
            catch (Exception ex)
            {
                SetStatus("Geocoding failed.", "#ef4444");
                MessageBox.Show(ex.Message, "Geocoding Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Florida bounds check
            if (!InFlorida(origin) && !InFlorida(dest))
            {
                SetStatus("Both locations are outside Florida.", "#ef4444");
                MessageBox.Show("Both locations are outside Florida.\n\nThis app only supports routes within Florida.",
                                "Out of Range", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!InFlorida(origin))
            {
                SetStatus("Origin is outside Florida.", "#ef4444");
                MessageBox.Show($"'{originText}' is outside Florida.\n\nPlease enter an origin within Florida.",
                                "Out of Range", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!InFlorida(dest))
            {
                SetStatus("Destination is outside Florida.", "#ef4444");
                MessageBox.Show($"'{destText}' is outside Florida.\n\nPlease enter a destination within Florida.",
                                "Out of Range", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetStatus("Calculating route…", "#64748b");

            // Get route from OSRM
            RouteResult? route;
            try
            {
                route = await GetRoute(origin, dest);
            }
            catch (Exception ex)
            {
                SetStatus("Routing failed.", "#ef4444");
                MessageBox.Show(ex.Message, "Routing Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Update distance & duration labels
            double km = route.DistanceM / 1000.0;
            double mi = km * 0.621371;
            int hours = (int)(route.DurationS / 3600);
            int mins  = (int)((route.DurationS % 3600) / 60);
            string dur = hours > 0 ? $"{hours} hr {mins} min" : $"{mins} min";

            DistanceLabel.Text = $"Distance: {km:F1} km  ({mi:F1} mi)";
            DurationLabel.Text = $"Duration: {dur}";
            PointsLabel.Text   = $"{route.Coordinates.Count} path points";

            SetStatus("Route loaded successfully.", "#22c55e");

            // Draw on map
            if (_mapReady)
                await DrawRoute(origin, dest, route.Coordinates);
        }

        // ── Draw route on Mapbox map ──────────────────────────────────────
        private async Task DrawRoute(GeoPoint origin, GeoPoint dest, List<double[]> coords)
        {
            // Build GeoJSON coordinates string
            var sb = new System.Text.StringBuilder();
            sb.Append('[');
            for (int i = 0; i < coords.Count; i++)
            {
                sb.Append($"[{coords[i][0].ToString(System.Globalization.CultureInfo.InvariantCulture)},{coords[i][1].ToString(System.Globalization.CultureInfo.InvariantCulture)}]");
                if (i < coords.Count - 1) sb.Append(',');
            }
            sb.Append(']');

            string js = $@"
                // Remove existing layers/sources
                if (map.getLayer('route-line'))   map.removeLayer('route-line');
                if (map.getLayer('origin-pt'))    map.removeLayer('origin-pt');
                if (map.getLayer('dest-pt'))      map.removeLayer('dest-pt');
                if (map.getSource('route'))       map.removeSource('route');
                if (map.getSource('origin'))      map.removeSource('origin');
                if (map.getSource('dest'))        map.removeSource('dest');

                // Route line
                map.addSource('route', {{
                    type: 'geojson',
                    data: {{
                        type: 'Feature',
                        geometry: {{ type: 'LineString', coordinates: {sb} }}
                    }}
                }});
                map.addLayer({{
                    id: 'route-line',
                    type: 'line',
                    source: 'route',
                    paint: {{ 'line-color': '#3b82f6', 'line-width': 5, 'line-opacity': 0.9 }}
                }});

                // Origin marker
                map.addSource('origin', {{
                    type: 'geojson',
                    data: {{ type: 'Feature', geometry: {{ type: 'Point', coordinates: [{origin.Lon.ToString(System.Globalization.CultureInfo.InvariantCulture)},{origin.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}] }} }}
                }});
                map.addLayer({{
                    id: 'origin-pt', type: 'circle', source: 'origin',
                    paint: {{ 'circle-radius': 10, 'circle-color': '#3b82f6', 'circle-stroke-width': 3, 'circle-stroke-color': '#fff' }}
                }});

                // Destination marker
                map.addSource('dest', {{
                    type: 'geojson',
                    data: {{ type: 'Feature', geometry: {{ type: 'Point', coordinates: [{dest.Lon.ToString(System.Globalization.CultureInfo.InvariantCulture)},{dest.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}] }} }}
                }});
                map.addLayer({{
                    id: 'dest-pt', type: 'circle', source: 'dest',
                    paint: {{ 'circle-radius': 10, 'circle-color': '#ef4444', 'circle-stroke-width': 3, 'circle-stroke-color': '#fff' }}
                }});

                // Fit map to route bounds
                var lats = {sb}.map(c => c[1]);
                var lons = {sb}.map(c => c[0]);
                map.fitBounds([
                    [Math.min(...lons), Math.min(...lats)],
                    [Math.max(...lons), Math.max(...lats)]
                ], {{ padding: 60, duration: 1000 }});
            ";

            await MapView.CoreWebView2.ExecuteScriptAsync(js);
        }

        // ── Geocoding — Nominatim ─────────────────────────────────────────
        private async Task<GeoPoint> Geocode(string query)
        {
            var strategies = new List<string> { query };

            // Simplify: strip business name prefix
            var parts = query.Split(',');
            if (parts.Length >= 2)
                strategies.Add(string.Join(",", parts[^2..]));

            foreach (var attempt in strategies)
            {
                var encoded = Uri.EscapeDataString(attempt);
                var url = $"https://nominatim.openstreetmap.org/search?q={encoded}&format=json&limit=1&countrycodes=us&bounded=1&viewbox=-87.634938,24.396308,-79.974306,31.000968";

                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("User-Agent", "FloridaRouteApp/1.0");
                var res = await Http.SendAsync(req);
                var json = await res.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(json);
                var arr = doc.RootElement;
                if (arr.GetArrayLength() > 0)
                {
                    var first = arr[0];
                    return new GeoPoint(
                        double.Parse(first.GetProperty("lat").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                        double.Parse(first.GetProperty("lon").GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                    );
                }
            }

            throw new Exception(
                $"Could not find location: '{query}'.\n\n" +
                "Tips:\n" +
                "• Use a city name: Miami, FL\n" +
                "• Or an address: 145 NW 29th St, Miami, FL\n" +
                "• Avoid business names");
        }

        // ── Routing — OSRM ───────────────────────────────────────────────
        private async Task<RouteResult> GetRoute(GeoPoint origin, GeoPoint dest)
        {
            var url = $"https://router.project-osrm.org/route/v1/driving/" +
                      $"{origin.Lon.ToString(System.Globalization.CultureInfo.InvariantCulture)},{origin.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture)};" +
                      $"{dest.Lon.ToString(System.Globalization.CultureInfo.InvariantCulture)},{dest.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                      "?overview=full&geometries=geojson&steps=false";

            var json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.GetProperty("code").GetString() != "Ok")
                throw new Exception(root.TryGetProperty("message", out var msg)
                    ? msg.GetString() : "No route found.");

            var route    = root.GetProperty("routes")[0];
            var distance = route.GetProperty("distance").GetDouble();
            var duration = route.GetProperty("duration").GetDouble();
            var rawCoords = route.GetProperty("geometry").GetProperty("coordinates");

            var coords = new List<double[]>();
            foreach (var c in rawCoords.EnumerateArray())
                coords.Add(new[] { c[0].GetDouble(), c[1].GetDouble() });

            // Simplify with RDP to keep rendering fast
            coords = RdpSimplify(coords, epsilon: 0.0003);
            if (coords.Count > 80)
            {
                int step = coords.Count / 80;
                var thinned = new List<double[]>();
                for (int i = 0; i < coords.Count; i += step) thinned.Add(coords[i]);
                if (thinned[^1] != coords[^1]) thinned.Add(coords[^1]);
                coords = thinned;
            }

            return new RouteResult(distance, duration, coords);
        }

        // ── Ramer-Douglas-Peucker simplification ─────────────────────────
        private static List<double[]> RdpSimplify(List<double[]> pts, double epsilon)
        {
            if (pts.Count < 3) return pts;

            double MaxDist(double[] p, double[] a, double[] b)
            {
                double dx = b[0] - a[0], dy = b[1] - a[1];
                if (dx == 0 && dy == 0) return Math.Sqrt(Math.Pow(p[0]-a[0],2)+Math.Pow(p[1]-a[1],2));
                double t = Math.Max(0, Math.Min(1, ((p[0]-a[0])*dx + (p[1]-a[1])*dy) / (dx*dx+dy*dy)));
                return Math.Sqrt(Math.Pow(p[0]-(a[0]+t*dx),2) + Math.Pow(p[1]-(a[1]+t*dy),2));
            }

            double dmax = 0; int idx = 0;
            for (int i = 1; i < pts.Count - 1; i++)
            {
                double d = MaxDist(pts[i], pts[0], pts[^1]);
                if (d > dmax) { dmax = d; idx = i; }
            }

            if (dmax > epsilon)
            {
                var left  = RdpSimplify(pts[..( idx+1)], epsilon);
                var right = RdpSimplify(pts[idx..],      epsilon);
                left.RemoveAt(left.Count - 1);
                left.AddRange(right);
                return left;
            }
            return new List<double[]> { pts[0], pts[^1] };
        }

        // ── Helpers ───────────────────────────────────────────────────────
        private static bool InFlorida(GeoPoint p) =>
            p.Lat >= MinLat && p.Lat <= MaxLat && p.Lon >= MinLon && p.Lon <= MaxLon;

        private void SetStatus(string msg, string hex = "#64748b")
        {
            Dispatcher.Invoke(() =>
            {
                StatusLabel.Text = msg;
                StatusLabel.Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(hex));
            });
        }

        // ── Build the HTML page with Mapbox GL JS ─────────────────────────
        private string BuildMapHtml() => $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'/>
    <meta name='viewport' content='width=device-width, initial-scale=1'/>
    <link href='https://api.mapbox.com/mapbox-gl-js/v3.3.0/mapbox-gl.css' rel='stylesheet'/>
    <script src='https://api.mapbox.com/mapbox-gl-js/v3.3.0/mapbox-gl.js'></script>
    <style>
        * {{ margin:0; padding:0; box-sizing:border-box; }}
        body, html {{ width:100%; height:100%; background:#0f1117; }}
        #map {{ width:100%; height:100vh; }}
    </style>
</head>
<body>
    <div id='map'></div>
    <script>
        mapboxgl.accessToken = '{MapboxToken}';
        var map = new mapboxgl.Map({{
            container: 'map',
            style: 'mapbox://styles/mapbox/dark-v11',
            center: [-81.76, 27.99],
            zoom: 6.5,
            attributionControl: false
        }});
        map.addControl(new mapboxgl.NavigationControl(), 'bottom-right');
        map.addControl(new mapboxgl.AttributionControl({{ compact: true }}));
    </script>
</body>
</html>";
    }

    // ── Data models ───────────────────────────────────────────────────────
    public record GeoPoint(double Lat, double Lon);
    public record RouteResult(double DistanceM, double DurationS, List<double[]> Coordinates);
}
