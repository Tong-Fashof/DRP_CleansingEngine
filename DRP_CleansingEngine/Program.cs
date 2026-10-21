using Microsoft.Data.SqlClient;

class Program
{
    private static string? targetWarehouseCode = null;

    static void Main(string[] args)
    {
        try
        {
            using SqlConnection connection = ConnectData();

            // =====================================================
            // ถ้าสั่งรันด้วย --test ให้รัน Unit Test แล้วจบโปรแกรม ไม่ต่อฐานข้อมูล
            // =====================================================
            if (args.Contains("--test"))
            {
                DRP_CleansingEngine.Tests.RunUnitTests();
                return;
            }

            // รับ Argument จาก Command Line (เช่น --warehouse CMPMH)
            if (args.Contains("--warehouse"))
            {
                int index = Array.IndexOf(args, "--warehouse");
                if (index + 1 < args.Length)
                {
                    targetWarehouseCode = args[index + 1];
                }
            }


            // โหลดค่า ADI Threshold
            decimal adiThreshold = LoadAdiThreshold(connection);

            //// โหลดข้อมูล SKU และปริมาณความต้องการ 24 เดือน
            List<SkuCleansingCandidate> candidates = LoadCandidates(connection);

            // เรียกใช้งาน Cleansing Engine เพื่อประเมินผล FM-03 -> FM-07
            CleansingEngine.CleansingCenterAll(candidates, adiThreshold);

            // Summary Report
            Console.WriteLine($"Total   : {candidates.Count}");
            Console.WriteLine($"KEEP    : {candidates.Count(c => c.CleansingStatus == "KEEP")}");
            Console.WriteLine($"PENDING : {candidates.Count(c => c.CleansingStatus == "PENDING")}");

            Console.WriteLine();
            Console.WriteLine("--- แยกตามเหตุผล ---");
            foreach (var g in candidates.GroupBy(c => c.CleansingReason ?? "PENDING").OrderByDescending(g => g.Count()))
            {
                Console.WriteLine($"{g.Key,-40} {g.Count(),8:N0}");
            }

            Console.WriteLine();
            Console.WriteLine("--- แยกตามคลัง ---");
            foreach (var g in candidates.GroupBy(c => c.warehouse_code).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                int k = g.Count(c => c.CleansingStatus == "KEEP");
                Console.WriteLine($"{g.Key,-12} รวม {g.Count(),7:N0} | KEEP {k,7:N0} | PENDING {g.Count() - k,7:N0}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"เกิดข้อผิดพลาด: {ex.Message}");
            throw;
        }
    }


    // ======================================================
    // ฟังก์ชันสำหรับเชื่อมต่อฐานข้อมูลและดึงข้อมูล
    // ======================================================
    static SqlConnection ConnectData()
    {
        string connectionString =
            "Server=192.168.2.117,9823;" +
            "Database=AxWebReport_UAT;" +
            "User ID=dev;" +
            "Password=P@ssw0rd22;" +
            "TrustServerCertificate=True;";

        SqlConnection connection = new SqlConnection(connectionString);

        try
        {
            connection.Open();
            Console.WriteLine("Connection successful!");
            return connection;
        }
        catch (Exception)
        {
            connection.Dispose();
            throw;
        }
    }


    // ======================================================
    // ฟังก์ชันสำหรับโหลดค่า ADI Threshold และข้อมูล SKU
    // ======================================================
    static decimal LoadAdiThreshold(SqlConnection connection)
    {
        // แก้ LOV_VALUE ให้ตรงกับชื่อคอลัมน์จริงใน M_LOV
        string sql = @"
            SELECT TOP (1)
              LOV_VALUE 
            FROM
              AxWebReport_UAT.dbo.M_LOV 
            WHERE
              LOV_CODE_1 = 'DRP_CONFIG' 
              AND LOV_CODE_2 = 'ADI_THRESHOLD' 
              AND (REC_STATUS = 'A' OR REC_STATUS IS NULL);";

        using SqlCommand cmd = new SqlCommand(sql, connection);
        object? result = cmd.ExecuteScalar();
        if (result == null || result == DBNull.Value)
        {
            Console.WriteLine("[WARN] ใช้ค่า Default ADI Threshold = 1.32");
            return 1.32m;
        }

        return Convert.ToDecimal(result);
    }


    // ======================================================
    // ฟังก์ชันสำหรับโหลดข้อมูล SKU และปริมาณความต้องการ 24 เดือน
    // ======================================================
    static List<SkuCleansingCandidate> LoadCandidates(SqlConnection connection)
    {
        string sql = @"
            SELECT 
                warehouse_code,
                item_id,
                month_no,
                net_demand_qty
            FROM DRP_FCT_SKU_MONTHLY_DEMAND_PREP
            WHERE (@TargetWarehouseCode IS NULL OR warehouse_code = @TargetWarehouseCode)";

        // สร้าง Dictionary เพื่อเก็บข้อมูล SKU และปริมาณความต้องการ 24 เดือน จาก Query
        var map = new Dictionary<string, SkuCleansingCandidate>();

        using SqlCommand cmd = new SqlCommand(sql, connection);

        // กำหนดค่า Parameter สำหรับ TargetWarehouseCode
        cmd.Parameters.AddWithValue("@TargetWarehouseCode", (object?)targetWarehouseCode ?? DBNull.Value);

        using SqlDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string warehouse_code = reader["warehouse_code"].ToString()!;
            string item_id = reader["item_id"].ToString()!;
            int month_no = Convert.ToInt32(reader["month_no"]);
            decimal net_demand_qty = reader["net_demand_qty"] == DBNull.Value ? 0m : Convert.ToDecimal(reader["net_demand_qty"]);

            if (month_no < 1 || month_no > 24) continue;        // ตรวจสอบ monthNo อยู่ในช่วง 1-24
            string key = warehouse_code + "|" + item_id;        // ใช้ WarehouseCode และ ItemId เป็น Key
            if (!map.ContainsKey(key))                          // ตรวจสอบว่ามี Key อยู่ใน Dictionary (ตาราง DRP_FCT_SKU_MONTHLY_DEMAND_PREP) หรือไม่ ถ้าไม่มีให้สร้าง SkuCleansingCandidate ใหม่
            {
                map[key] = new SkuCleansingCandidate { warehouse_code = warehouse_code, item_id = item_id };
            }


            // ยอดความต้องการแท้จริง 24 เดือนและการประกอบอาเรย์(Array Population): ดึงชุดข้อมูล net_demand_qty เรียงตาม month_no(1..24) รายคลังและราย SKU จากตาราง DRP_FCT_SKU_MONTHLY_DEMAND_PREP 
            // โดยสร้างอาเรย์ decimal[24] กำหนดค่าเริ่มต้นเป็น 0.0000m ทั้งหมด แล้วแมปข้อมูลตามตำแหน่ง Demand24M[month_no - 1] = net_demand_qty เพื่อให้มั่นใจว่าอาเรย์มีขนาด 24 งวดสมบูรณ์ 100 % แม้บางงวดจะไม่มีข้อมูลในฐานข้อมูล

            // เพิ่มค่า net_demand_qty ลงใน Demand24M ของ SkuCleansingCandidate ตาม month_no
            map[key].Demand24M[month_no - 1] += net_demand_qty;
        }

        return map.Values.ToList();
    }

}
