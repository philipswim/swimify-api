using ClosedXML.Excel;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;


namespace SwimResultsFetcher
{
    public partial class MainWindow : Window
    {
        private readonly HttpClient _httpClient = new HttpClient();
        private string _serverBaseUrl;
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        private readonly List<JsonNode> _rawSummaryJsonList = new List<JsonNode>();

        private readonly ObservableCollection<ResultDisplayRow> _allResults = new ObservableCollection<ResultDisplayRow>();
        private readonly ObservableCollection<ClubScoreRow> _clubScores = new ObservableCollection<ClubScoreRow>();
        private Dictionary<string, int> _previousClubRanks = new Dictionary<string, int>();
        
        private Dictionary<string, int> _previousClubPoints = new Dictionary<string, int>();
        private Dictionary<string, (string Symbol, string Color)> _lastKnownBadges = new Dictionary<string, (string, string)>();

        private readonly DispatcherTimer _autoRefreshTimer = new DispatcherTimer();

        private const string AllEventsOption = "Alle løb";
        private bool _isFetching = false;
        private readonly ObservableCollection<BaseTimeRow> _baseTimesList = new ObservableCollection<BaseTimeRow>();
        private Dictionary<int, double> _baseTimeLookup = new Dictionary<int, double>();


        public MainWindow()
        {
            InitializeComponent();
            DgResults.ItemsSource = _allResults;
            IcClubScores.ItemsSource = _clubScores;
            DgBaseTimes.ItemsSource = _baseTimesList;


            ResetEventDropdown();

            _autoRefreshTimer.Interval = TimeSpan.FromSeconds(30);
            _autoRefreshTimer.Tick += async (s, e) => await FetchDataAsync();
        }

        private void ResetEventDropdown()
        {
            CmbEvents.Items.Clear();
            CmbEvents.Items.Add(AllEventsOption);
            CmbEvents.SelectedIndex = 0;
        }

        private async void BtnFetch_Click(object sender, RoutedEventArgs e)
        {
            await FetchDataAsync();
        }

        private async Task FetchDataAsync()
        {
            if (_isFetching) return;

            string ip = TxtIpAddress.Text.Trim();
            if (string.IsNullOrEmpty(ip))
            {
                TxtStatus.Text = "Indtast venligst en gyldig IP-adresse.";
                return;
            }

            _isFetching = true;
            SetButtonsEnabled(false);

            // Husk nuværende valgte løb før opdatering
            string previouslySelectedEvent = CmbEvents.SelectedItem?.ToString() ?? AllEventsOption;

            try
            {
                _serverBaseUrl = $"http://{ip}:12000";
                TxtStatus.Text = $"Henter data fra {_serverBaseUrl}...";

                var fetchedResults = await LoadAllResultsAutomaticallyAsync();

                // Opdater resultattabel
                _allResults.Clear();
                foreach (var item in fetchedResults)
                {
                    _allResults.Add(item);
                }

                // Opdater løbs-dropdown og gendan valg
                UpdateEventDropdown(fetchedResults, previouslySelectedEvent);

                // Beregn og opdater Live Point Scoreboard
                UpdateClubScoreboard(fetchedResults);

                // Videresend JSON til de to eksterne API'er hvis aktiveret
                if (ChkAutoForward.IsChecked == true)
                {
                    await ForwardDataToApisAsync();
                }

                TxtStatus.Text = $"Sidst opdateret kl. {DateTime.Now:HH:mm:ss} — {_allResults.Count} resultater fordelt på {_clubScores.Count} klubber.";
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Fejl ved forbindelse til {ip}: {ex.Message}";
            }
            finally
            {
                SetButtonsEnabled(true);
                _isFetching = false;
            }
        }

        private async Task<List<ResultDisplayRow>> LoadAllResultsAutomaticallyAsync()
        {
            var tempResults = new List<ResultDisplayRow>();
            _rawSummaryJsonList.Clear();

            // 1. Hent stævnenavn
            string compJson = await _httpClient.GetStringAsync($"{_serverBaseUrl}/competition");
            var comp = JsonSerializer.Deserialize<Competition>(compJson, _jsonOptions);
            TxtCompetition.Text = $"{comp?.Name} - {comp?.Location}";

            // 2. Hent alle afsnit
            string sessionsJson = await _httpClient.GetStringAsync($"{_serverBaseUrl}/sessions");
            var sessions = JsonSerializer.Deserialize<List<Session>>(sessionsJson, _jsonOptions);
            if (sessions == null) return tempResults;

            // 3. Saml alle rounds fra alle afsnit
            var allRounds = new List<Round>();
            foreach (var session in sessions)
            {
                try
                {
                    TxtStatus.Text = $"Henter program for afsnit {session.Number} ({session.Name})...";
                    string sessionDetailJson = await _httpClient.GetStringAsync($"{_serverBaseUrl}/session?number={session.Number}");
                    var fullSession = JsonSerializer.Deserialize<Session>(sessionDetailJson, _jsonOptions);
                    if (fullSession?.Rounds != null)
                    {
                        allRounds.AddRange(fullSession.Rounds);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Fejl i afsnit {session.Number}: {ex.Message}");
                }
            }

            // 4. Hent resultater for hvert løb
            int current = 0;
            foreach (var round in allRounds)
            {
                current++;
                TxtStatus.Text = $"Henter løb {current} af {allRounds.Count}: {round.TimeProgramText}...";

                if (round.RoundOid <= 0) continue;

                try
                {
                    string summaryJson = await _httpClient.GetStringAsync($"{_serverBaseUrl}/summary?roundoid={round.RoundOid}");
                    var summary = JsonSerializer.Deserialize<SummaryResponse>(summaryJson, _jsonOptions);

                    if (summary?.Results != null && summary.Results.Count > 0)
                    {
                        int eventNum = summary.Event?.Number ?? 0;
                        string eventLabel = summary.Event != null
                            ? $"Løb {eventNum}: {summary.Event.Name}"
                            : round.TimeProgramText ?? $"Løb {eventNum}";

                        // Gem det rå JSON-objekt 100 % urørt til videresendelse
                        JsonNode? rawNode = JsonNode.Parse(summaryJson);
                        if (rawNode != null)
                        {
                            _rawSummaryJsonList.Add(rawNode);
                        }

                        // Beregn visning og point til programmets egne faner
                        foreach (var res in summary.Results)
                        {
                            double swimSeconds = (res.DSQ || res.DNS || res.DNF)
                                ? 0
                                : ParseTimeToSeconds(res.ResultTime ?? "");

                            int points = res.FINAPoints > 0
                                ? res.FINAPoints
                                : CalculateCustomPoints(eventNum, swimSeconds);

                            tempResults.Add(new ResultDisplayRow
                            {
                                EventNumber = eventNum,
                                EventTitle = eventLabel,
                                Rank = res.TotalRank > 0 ? $"{res.TotalRank}." : "-",
                                SwimmerName = res.FullName,
                                Club = string.IsNullOrWhiteSpace(res.Club) ? "Ukendt klub" : res.Club.Trim(),
                                Time = res.DisplayTime,
                                ResultTimeSeconds = swimSeconds,
                                FinaPoints = points
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Sprang RoundOid {round.RoundOid} over: {ex.Message}");
                }
            }

            return tempResults;
        }

        private void UpdateClubScoreboard(List<ResultDisplayRow> results)
        {
            var groupedClubs = results
                .Where(r => r.FinaPoints > 0)
                .GroupBy(r => r.Club)
                .Select(g => new
                {
                    Club = g.Key,
                    TotalPoints = g.Sum(x => x.FinaPoints),
                    Count = g.Count()
                })
                .OrderByDescending(x => x.TotalPoints)
                .ToList();

            // Tjek om der overhovedet er sket ændringer i pointene siden sidst
            var currentPoints = groupedClubs.ToDictionary(c => c.Club, c => c.TotalPoints);
            bool hasPointsChanged = _previousClubPoints.Count != currentPoints.Count ||
                                    currentPoints.Any(kvp => !_previousClubPoints.TryGetValue(kvp.Key, out int oldPts) || oldPts != kvp.Value);

            var newRanks = new Dictionary<string, int>();
            _clubScores.Clear();

            int rank = 1;
            int previousClubPoints = 0;

            foreach (var club in groupedClubs)
            {
                newRanks[club.Club] = rank;

                if (rank <= 12)
                {
                    string symbol = "—";
                    string color = "#64748B";

                    if (!hasPointsChanged && _lastKnownBadges.TryGetValue(club.Club, out var savedBadge))
                    {
                        // Ingen nye point i stævnet -> behold de nuværende pile!
                        symbol = savedBadge.Symbol;
                        color = savedBadge.Color;
                    }
                    else if (_previousClubRanks.TryGetValue(club.Club, out int oldRank))
                    {
                        // Der er kommet nye point -> beregn ny forskel i placering
                        int diff = oldRank - rank;
                        if (diff > 0)
                        {
                            symbol = $"▲ +{diff}";
                            color = "#22C55E";
                        }
                        else if (diff < 0)
                        {
                            symbol = $"▼ {diff}";
                            color = "#EF4444";
                        }
                    }
                    else if (_previousClubRanks.Count > 0)
                    {
                        symbol = "★ NY";
                        color = "#FACC15";
                    }

                    // Gem den viste pil/status for klubben
                    _lastKnownBadges[club.Club] = (symbol, color);

                    int diffToAbove = rank == 1 ? 0 : previousClubPoints - club.TotalPoints;

                    _clubScores.Add(new ClubScoreRow
                    {
                        Rank = rank,
                        RankChangeSymbol = symbol,
                        RankChangeColor = color,
                        Club = club.Club,
                        TotalFinaPoints = club.TotalPoints,
                        SwimCount = club.Count,
                        PointDiff = diffToAbove
                    });
                }

                previousClubPoints = club.TotalPoints;
                rank++;
            }

            // Opdater kun reference-listerne, når der faktisk er kommet nye point
            if (hasPointsChanged)
            {
                _previousClubRanks = newRanks;
                _previousClubPoints = currentPoints;
            }
        }


        private void UpdateEventDropdown(List<ResultDisplayRow> results, string selectedToRestore)
        {
            var distinctEvents = results.Select(r => r.EventTitle).Distinct().ToList();

            CmbEvents.Items.Clear();
            CmbEvents.Items.Add(AllEventsOption);
            foreach (var ev in distinctEvents)
            {
                CmbEvents.Items.Add(ev);
            }

            CmbEvents.SelectedItem = CmbEvents.Items.Contains(selectedToRestore)
                ? selectedToRestore
                : AllEventsOption;
        }

        private void FilterChanged(object sender, EventArgs e)
        {
            if (DgResults == null || CmbEvents == null || TxtFilter == null) return;

            var view = CollectionViewSource.GetDefaultView(DgResults.ItemsSource);
            if (view == null) return;

            string selectedEvent = CmbEvents.SelectedItem?.ToString();
            string filterText = TxtFilter.Text.Trim().ToLower();

            view.Filter = item =>
            {
                if (item is ResultDisplayRow row)
                {
                    bool matchesEvent = string.IsNullOrEmpty(selectedEvent) ||
                                        selectedEvent == AllEventsOption ||
                                        row.EventTitle == selectedEvent;

                    bool matchesText = string.IsNullOrEmpty(filterText) ||
                                       (row.SwimmerName?.ToLower().Contains(filterText) ?? false) ||
                                       (row.Club?.ToLower().Contains(filterText) ?? false);

                    return matchesEvent && matchesText;
                }
                return false;
            };
        }

        private void ChkAutoRefresh_Changed(object sender, RoutedEventArgs e)
        {
            if (ChkAutoRefresh.IsChecked == true)
                _autoRefreshTimer.Start();
            else
                _autoRefreshTimer.Stop();
        }

        private async void BtnUdpScan_Click(object sender, RoutedEventArgs e)
        {
            SetButtonsEnabled(false);
            TxtStatus.Text = "Søger efter server via UDP (9891 -> 9892)...";

            try
            {
                string foundIp = await DiscoverServerIpAsync();
                if (!string.IsNullOrEmpty(foundIp))
                {
                    TxtIpAddress.Text = foundIp;
                    TxtStatus.Text = $"Server fundet på {foundIp}! Klik på 'Hent Alle Resultater'.";
                }
                else
                {
                    TxtStatus.Text = "Ingen server svarede på UDP. Indtast IP manuelt.";
                }
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"UDP-fejl: {ex.Message}";
            }
            finally
            {
                SetButtonsEnabled(true);
            }
        }
        private async Task<string> DiscoverServerIpAsync()
        {
            using var udpListener = new UdpClient(9892);
            using var udpSender = new UdpClient();
            udpSender.EnableBroadcast = true;

            var receiveTask = udpListener.ReceiveAsync();
            byte[] pingBytes = Encoding.UTF8.GetBytes("PING");
            await udpSender.SendAsync(pingBytes, pingBytes.Length, new IPEndPoint(IPAddress.Broadcast, 9891));

            var completedTask = await Task.WhenAny(receiveTask, Task.Delay(3000));
            if (completedTask == receiveTask)
            {
                var result = await receiveTask;
                return result.RemoteEndPoint.Address.ToString();
            }

            return null;
        }

        private void SetButtonsEnabled(bool isEnabled)
        {
            BtnFetch.IsEnabled = isEnabled;
            BtnUdpScan.IsEnabled = isEnabled;
        }
        private void BtnLoadExcel_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Excel-filer (*.xlsx)|*.xlsx",
                Title = "Vælg Excel-fil med basistider"
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                _baseTimesList.Clear();
                _baseTimeLookup.Clear();

                using var workbook = new XLWorkbook(dialog.FileName);
                var worksheet = workbook.Worksheets.First();
                var rows = worksheet.RangeUsed()?.RowsUsed();

                if (rows != null)
                {
                    foreach (var row in rows)
                    {
                        // Spring evt. overskriftsrække over, hvis kolonne A ikke er et tal
                        if (!int.TryParse(row.Cell(1).GetValue<string>().Trim(), out int eventNumber))
                            continue;

                        string rawTime = row.Cell(2).GetFormattedString().Trim();
                        double seconds = ParseTimeToSeconds(rawTime);

                        if (seconds > 0)
                        {
                            _baseTimeLookup[eventNumber] = seconds;
                            _baseTimesList.Add(new BaseTimeRow
                            {
                                EventNumber = eventNumber,
                                BaseTimeText = rawTime,
                                BaseTimeSeconds = seconds
                            });
                        }
                    }
                }

                TxtExcelStatus.Text = $"Indlæste {_baseTimeLookup.Count} basistider fra {System.IO.Path.GetFileName(dialog.FileName)}.";

                // Genberegn point på allerede hentede resultater med det samme
                if (_allResults.Count > 0)
                {
                    RecalculateMissingPoints();
                    UpdateClubScoreboard(_allResults.ToList());
                    DgResults.Items.Refresh();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Kunne ikke læse Excel-filen: {ex.Message}", "Fejl", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Omregner f.eks. "3:20.78", "00:56.98" eller "56.98" til sekunder
        private double ParseTimeToSeconds(string timeText)
        {
            if (string.IsNullOrWhiteSpace(timeText)) return 0;

            // Ensret komma til punktum for decimaler
            string clean = timeText.Trim().Replace(',', '.');
            string[] parts = clean.Split(':');

            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double mins) &&
                double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double secs))
            {
                return (mins * 60.0) + secs;
            }

            if (parts.Length == 1 &&
                double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double onlySecs))
            {
                return onlySecs;
            }

            return 0;
        }

        // Din præcise formel: 1000 * (Basistid / Svømmetid)^3 rundet ned
        private int CalculateCustomPoints(int eventNumber, double swimTimeSeconds)
        {
            if (swimTimeSeconds <= 0) return 0;
            if (!_baseTimeLookup.TryGetValue(eventNumber, out double baseSeconds) || baseSeconds <= 0)
                return 0;

            double ratio = baseSeconds / swimTimeSeconds;
            double rawPoints = 1000.0 * Math.Pow(ratio, 3);
            return (int)Math.Floor(rawPoints);
        }

        private void RecalculateMissingPoints()
        {
            foreach (var row in _allResults)
            {
                if (row.FinaPoints == 0 && row.ResultTimeSeconds > 0)
                {
                    row.FinaPoints = CalculateCustomPoints(row.EventNumber, row.ResultTimeSeconds);
                }
            }
        }

        private async void BtnForwardNow_Click(object sender, RoutedEventArgs e)
        {
            await ForwardDataToApisAsync();
        }

        private async Task ForwardDataToApisAsync()
        {
            string resultsUrl = TxtResultsApiUrl.Text.Trim();
            string standingsUrl = TxtStandingsApiUrl.Text.Trim();

            if (string.IsNullOrEmpty(resultsUrl) && string.IsNullOrEmpty(standingsUrl))
            {
                TxtForwardStatus.Text = "Ingen API URL'er indtastet.";
                return;
            }

            var exportOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            var statusMessages = new List<string>();

            // 1. Send den samlede liste af originale løbs-JSON'er (med Event, Round, results osv.)
            if (!string.IsNullOrEmpty(resultsUrl) && _rawSummaryJsonList.Count > 0)
            {
                try
                {
                    string json = JsonSerializer.Serialize(_rawSummaryJsonList, exportOptions);
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var response = await _httpClient.PostAsync(resultsUrl, content);

                    statusMessages.Add(response.IsSuccessStatusCode
                        ? $"Resultater sendt OK ({_rawSummaryJsonList.Count} løb, HTTP {(int)response.StatusCode})"
                        : $"Resultater fejl: {(int)response.StatusCode} {response.ReasonPhrase}");
                }
                catch (Exception ex)
                {
                    statusMessages.Add($"Resultater fejl: {ex.Message}");
                }
            }

            // 2. Send samlet klubstilling
            if (!string.IsNullOrEmpty(standingsUrl) && _clubScores.Count > 0)
            {
                try
                {
                    var payload = new
                    {
                        UpdatedAt = DateTime.Now,
                        Competition = TxtCompetition.Text,
                        Standings = _clubScores
                    };

                    string json = JsonSerializer.Serialize(payload, exportOptions);
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var response = await _httpClient.PostAsync(standingsUrl, content);

                    statusMessages.Add(response.IsSuccessStatusCode
                        ? $"Stilling sendt OK ({(int)response.StatusCode})"
                        : $"Stilling fejl: {(int)response.StatusCode} {response.ReasonPhrase}");
                }
                catch (Exception ex)
                {
                    statusMessages.Add($"Stilling fejl: {ex.Message}");
                }
            }

            if (statusMessages.Count > 0)
            {
                TxtForwardStatus.Text = $"[{DateTime.Now:HH:mm:ss}] " + string.Join(" | ", statusMessages);
            }
        }


    }
}