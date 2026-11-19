using System.Collections.Generic;
using System.Globalization;


namespace SwimResultsFetcher
{

    public class Competition
    {
        public string Name { get; set; }
        public string Location { get; set; }
    }

    public class Session
    {
        public int SessionOid { get; set; }
        public int Number { get; set; }
        public string Name { get; set; }
        public List<Round> Rounds { get; set; }
    }

    public class Round
    {
        public int RoundOid { get; set; }
        public int Number { get; set; }
        public string Name { get; set; }
        public string TimeProgramText { get; set; }
    }

    public class SummaryResponse
    {
        public EventInfo Event { get; set; }
        public List<SwimmerResult> Results { get; set; }
    }

    public class EventInfo
    {
        public int EventOid { get; set; }
        public int Number { get; set; }
        public string Name { get; set; }
    }

    public class SwimmerResult
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Club { get; set; }
        public string ResultTime { get; set; }
        public int FINAPoints { get; set; }
        public int TotalRank { get; set; }
        public bool DSQ { get; set; }
        public bool DNS { get; set; }
        public bool DNF { get; set; }

        public string FullName => $"{FirstName} {LastName}".Trim();

        public string DisplayTime
        {
            get
            {
                if (DSQ) return "DSQ";
                if (DNS) return "DNS";
                if (DNF) return "DNF";
                return ResultTime;
            }
        }
    }

    // Bruges til den samlede visning i DataGrid
    public class ResultDisplayRow
    {
        public int EventNumber { get; set; }
        public string EventTitle { get; set; } = "";
        public string Rank { get; set; } = "";
        public string SwimmerName { get; set; } = "";
        public string Club { get; set; } = "";
        public string Time { get; set; } = "";
        public double ResultTimeSeconds { get; set; }
        public int FinaPoints { get; set; }
    }

    public class ClubScoreRow
    {
        public int Rank { get; set; }
        public string RankChangeSymbol { get; set; } // F.eks. "▲ +1", "▼ -1", "—"
        public string RankChangeColor { get; set; }  // Farvekode til pilen
        public string Club { get; set; }
        public int TotalFinaPoints { get; set; }
        public int SwimCount { get; set; }
        public double AveragePoints => SwimCount > 0 ? Math.Round((double)TotalFinaPoints / SwimCount, 1) : 0;

        public string FormattedPoints => TotalFinaPoints.ToString("N0", new CultureInfo("da-DK"));

        public int PointDiff { get; set; }
        public string FormattedDiff => Rank == 1 || PointDiff == 0
            ? "—"
            : $"-{PointDiff.ToString("N0", new CultureInfo("da-DK"))}";

        // Hjælper til medaljefarve på Top 3
        public string RankBadgeColor => Rank switch
        {
            1 => "#FFD700", // Guld
            2 => "#C0C0C0", // Sølv
            3 => "#CD7F32", // Bronze
            _ => "#E2E8F0"  // Standard grå
        };
    }
    // Tilføj denne property inde i ResultDisplayRow i Models.cs:
    // public double ResultTimeSeconds { get; set; }

    public class BaseTimeRow
    {
        public int EventNumber { get; set; }
        public string BaseTimeText { get; set; }
        public double BaseTimeSeconds { get; set; }
    }


}
