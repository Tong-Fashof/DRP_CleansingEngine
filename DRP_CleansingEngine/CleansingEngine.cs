public class SkuCleansingCandidate
{
    public string warehouse_code { get; set; } = "";
    public string item_id { get; set; } = "";
    public decimal[] Demand24M { get; set; } = new decimal[24];
    public int HistoryMonths { get; set; }
    public int ActiveMonths { get; set; }
    public decimal Adi { get; set; }
    public decimal Sd { get; set; }
    public string CleansingStatus { get; set; } = "PENDING";
    public string? CleansingMethod { get; set; }
    public string? CleansingReason { get; set; }
}

public static class CleansingEngine
{
    // ### Property คือข้อมูลหรือคุณสมบัติที่กำหนดไว้ใน SkuCleansingCandidate เพื่อให้สินค้าแต่ละรายการมีข้อมูลเหล่านั้น ###
    /* Property           ใช้เก็บอะไร
      HistoryMonths       จำนวนเดือนที่มีประวัติข้อมูล
      ActiveMonths        จำนวนเดือนที่มี Demand
      Adi                 ค่า Average Demand Interval
      Sd                  ค่า Standard Deviation
      CleansingStatus     สถานะ เช่น KEEP, PENDING
      CleansingMethod     วิธีจัดการข้อมูล
      CleansingReason     เหตุผลที่เลือกสถานะนั้น
      Demand24M           ข้อมูล Demand ย้อนหลัง 24 เดือน */

    public static void CleansingCenterAll(List<SkuCleansingCandidate> list, decimal adiThreshold)
    {
        foreach (var c in list)
        {
            GateEvaluate_1(c, adiThreshold);
        }
    }

    // Gate 1
    public static void GateEvaluate_1(SkuCleansingCandidate c, decimal adiThreshold)
    {
        c.HistoryMonths = CalculateHistoryMonths(c.Demand24M);
        c.ActiveMonths = CalculateActiveMonths(c.Demand24M);
        c.Adi = CalculateAdi(c.ActiveMonths);
        c.Sd = CalculateSd(c.Demand24M);

        // กำหนดข้อความเหตุผลตามลำดับเงื่อนไขก่อนหลัง(DEC - PBI03 - 02, อ้างอิง FM - 03, FM - 05, FM - 07):
        // หาก HistoryMonths< 12 → 'History < 12M'(FM - 03)
        // หาก Adi > ADI_THRESHOLD → 'Intermittent Demand (ADI > 1.32)'(FM - 05)
        // หาก Sd == 0.0000m → 'Zero Variance (SD = 0)'(FM - 07)
        // กรณี PENDING → null
        if (c.HistoryMonths < 12)
        {
            c.CleansingReason = "History < 12M";
        }
        else if (c.Adi > adiThreshold)
        {
            c.CleansingReason = "Intermittent Demand (ADI > 1.32)";
        }
        else if (c.Sd == 0m)
        {
            c.CleansingReason = "Zero Variance (SD = 0)";
        }
        else
        {
            c.CleansingReason = null;
        }


        if (c.CleansingReason != null)
        {
            c.CleansingStatus = "KEEP";
            c.CleansingMethod = "KEEP";
        }
        else
        {
            c.CleansingStatus = "PENDING";
            c.CleansingMethod = null;
        }
    }

    // FM-03: History Months - จำนวนเดือนประวัตินับจากเริ่มขายจริง: n
    public static int CalculateHistoryMonths(decimal[] demand)
    {
        int firstActiveMonth = 0;
        for (int i = 0; i < 24; i++)
        {
            if (demand[i] > 0m)
            {
                firstActiveMonth = i + 1;
                break;
            }
        }

        if (firstActiveMonth == 0) return 0;
        return 24 - firstActiveMonth + 1;
    }

    // FM-04: Active Months - จำนวนเดือนที่มีการขายจริง
    public static int CalculateActiveMonths(decimal[] demand)
    {
        int count = 0;
        for (int i = 0; i < 24; i++)
        {
            if (demand[i] > 0m) count++;
        }
        return count;
    }

    // FM-05: Average Demand Interval (ADI) - ช่วงห่างคำสั่งซื้อเฉลี่ย: ADI
    public static decimal CalculateAdi(int activeMonths)
    {
        if (activeMonths == 0) return 0m;
        return 24.0m / activeMonths;
    }

    // FM-06: Average Monthly Demand - ยอดขายเฉลี่ยต่อเดือน
    public static decimal CalculateAvgDemand(decimal[] demand)
    {
        decimal sum = 0m;
        for (int i = 0; i < 24; i++)
        {
            sum += demand[i];
        }

        return sum / 24.0m;
    }

    // FM-07: Sample Standard Deviation [SD (sample, n-1 = 23)] - ส่วนเบี่ยงเบนมาตรฐานของยอดขายต่อเดือน (SD)
    public static decimal CalculateSd(decimal[] demand)
    {
        decimal avg = CalculateAvgDemand(demand);
        decimal sumSqDiff = 0m;
        for (int i = 0; i < 24; i++)
        {
            decimal diff = demand[i] - avg;
            sumSqDiff += diff * diff;
        }

        double variance = (double)(sumSqDiff / 23.0m);
        //return Math.Round((decimal)Math.Sqrt(Math.Max(0.0, variance)), 4);
        return (decimal)Math.Sqrt(Math.Max(0.0, variance));
    }


}
