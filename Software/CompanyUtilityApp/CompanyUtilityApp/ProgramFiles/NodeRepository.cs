using CompanyUtilityApp.UserControls;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;

namespace CompanyUtilityApp.ProgramFiles
{
    public class Node
    {
        public int Id { get; set; }
        public int NodeNumber { get; set; }
        public int PanelSerialNumber { get; set; }   // FK to Areas
        public string LocalIPAddress { get; set; }

        // Calibration fields (will use DB defaults, not shown in UI yet)
        public double Zerovolt_CS1 { get; set; } = 2.40;
        public double Zerovolt_CS2 { get; set; } = 2.40;
        public double Sensitivity_CS1 { get; set; } = 0.105;
        public double Sensitivity_CS2 { get; set; } = 0.105;
        public double Threshold_CS1 { get; set; } = 0.100;
        public double Threshold_CS2 { get; set; } = 0.120;
        public double BatteryCalibration { get; set; } = 0.0;
        public double BatterySagCompensation { get; set; } = 0.0;
        public int PSUThreshold { get; set; } = 1800;
        public bool Calibration { get; set; } = false;
    }

    public class NodeDisplayItem
    {
        public int Route { get; set; }
        public int PanelLocation { get; set; }
        public int PanelSerialNumber { get; set; }
        public int NodeNumber { get; set; }       
        public string LocalIPAddress { get; set; }
        public string Description { get; set; }
        public bool Calibration { get; set; }     
    }

    public class PanelSettingsModel
    {
        public int Route { get; set; }
        public int PanelLocation { get; set; }
        public string IPAddress { get; set; }
        public string Description { get; set; }
        public int PanelSerialNumber { get; set; }
        //public override string ToString() => PanelLocation.ToString(); // for dropdown display
    }

    public static class NodeRepository
    {
        // ========== READ (joined view: From Areas, and Nodes) ==========
        public static List<NodeDisplayItem> GetAllNodesForRoute(int route)
        {
            var list = new List<NodeDisplayItem>();
            using (var conn = new SqlConnection(DatabaseHelper.ConnectionString))
            {
                conn.Open();
                string query = @"
            SELECT A.Route, A.PanelLocation, A.PanelSerialNumber,
                   N.NodeNumber, N.LocalIPAddress, A.Description, N.Calibration
            FROM Areas A
            INNER JOIN Nodes N ON A.PanelSerialNumber = N.PanelSerialNumber
            WHERE A.Route = @Route
            ORDER BY A.PanelLocation ASC";
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@Route", route);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            list.Add(new NodeDisplayItem
                            {
                                Route = reader.GetInt32(0),
                                PanelLocation = reader.GetInt32(1),
                                PanelSerialNumber = reader.GetInt32(2),
                                NodeNumber = reader.GetInt32(3),
                                LocalIPAddress = reader.GetString(4),
                                Description = reader.IsDBNull(5) ? null : reader.GetString(5),
                                Calibration = reader.GetBoolean(6)
                            });
                        }
                    }
                }
            }
            return list;
        }

        // ========== Get available Areas (no Node assigned) ==========
        public static List<Area> GetAvailableAreasForRoute(int route)
        {
            var list = new List<Area>();
            using (var conn = new SqlConnection(DatabaseHelper.ConnectionString))
            {
                conn.Open();
                string query = @"
                    SELECT Id, Route, PanelLocation, HoldingRegister, PanelSerialNumber, Description
                    FROM Areas
                    WHERE Route = @Route
                      AND PanelSerialNumber NOT IN (SELECT PanelSerialNumber FROM Nodes)
                    ORDER BY PanelLocation ASC";
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@Route", route);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            list.Add(new Area
                            {
                                Id = reader.GetInt32(0),
                                Route = reader.GetInt32(1),
                                PanelLocation = reader.GetInt32(2),
                                HoldingRegister = reader.GetInt32(3),
                                PanelSerialNumber = reader.GetInt32(4),
                                Description = reader.IsDBNull(5) ? null : reader.GetString(5)
                            });
                        }
                    }
                }
            }
            return list;
        }

        // ========== Get available Nodes (giving PSN as argument.) ==========
        public static Node GetNodeByPanelSerialNumber(int serial)
        {
            using (var conn = new SqlConnection(DatabaseHelper.ConnectionString))
            {
                conn.Open();
                string query = @"SELECT Id, NodeNumber, PanelSerialNumber, LocalIPAddress,
                                Zerovolt_CS1, Zerovolt_CS2, Sensitivity_CS1, Sensitivity_CS2,
                                Threshold_CS1, Threshold_CS2, BatteryCalibration,
                                BatterySagCompensation, PSUThreshold, Calibration
                                FROM Nodes WHERE PanelSerialNumber = @serial";
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@serial", serial);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            return new Node
                            {
                                Id = reader.GetInt32(0),
                                NodeNumber = reader.GetInt32(1),
                                PanelSerialNumber = reader.GetInt32(2),
                                LocalIPAddress = reader.GetString(3),
                                Zerovolt_CS1 = reader.GetDouble(4),
                                Zerovolt_CS2 = reader.GetDouble(5),
                                Sensitivity_CS1 = reader.GetDouble(6),
                                Sensitivity_CS2 = reader.GetDouble(7),
                                Threshold_CS1 = reader.GetDouble(8),
                                Threshold_CS2 = reader.GetDouble(9),
                                BatteryCalibration = reader.GetDouble(10),
                                BatterySagCompensation = reader.GetDouble(11),
                                PSUThreshold = reader.GetInt32(12),
                                Calibration = reader.GetBoolean(13)
                            };
                        }
                    }
                }
            }
            return null;
        }

        // --- CREATE a new node ---
        public static void AddNode(Node node)
        {
            using (var conn = new SqlConnection(DatabaseHelper.ConnectionString))
            {
                conn.Open();
                string query = @"
                    INSERT INTO Nodes (NodeNumber, PanelSerialNumber, LocalIPAddress, Calibration)
                    VALUES (@NodeNumber, @PanelSerialNumber, @LocalIPAddress, @Calibration)";
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@NodeNumber", node.NodeNumber);
                    cmd.Parameters.AddWithValue("@PanelSerialNumber", node.PanelSerialNumber);
                    cmd.Parameters.AddWithValue("@LocalIPAddress", node.LocalIPAddress);
                    cmd.Parameters.AddWithValue("@Calibration", node.Calibration);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // --- UPDATE an existing node ---
        public static void UpdateNode(Node node)
        {
            using (var conn = new SqlConnection(DatabaseHelper.ConnectionString))
            {
                conn.Open();
                string query = @"UPDATE Nodes SET NodeNumber = @NodeNumber,
                                 LocalIPAddress = @LocalIPAddress,
                                 Calibration = @Calibration
                                 WHERE Id = @Id";
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@Id", node.Id);
                    cmd.Parameters.AddWithValue("@NodeNumber", node.NodeNumber);
                    cmd.Parameters.AddWithValue("@LocalIPAddress", node.LocalIPAddress);
                    cmd.Parameters.AddWithValue("@Calibration", node.Calibration);
                    cmd.ExecuteNonQuery();
                }
            }
        }


        // --- DELETE a node ---
        public static void DeleteNode(int id)
        {
            using (var conn = new SqlConnection(DatabaseHelper.ConnectionString))
            {
                conn.Open();
                string query = "DELETE FROM Nodes WHERE Id = @Id";
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@Id", id);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // ------------------ The next two methods are used in NodesUserControl or AddEditNode-UserForm --- For Uniqueness checking.
        public static bool NodeNumberExists(int nodeNumber, int? excludeNodeId = null)
        {
            using (var conn = new SqlConnection(DatabaseHelper.ConnectionString))
            {
                conn.Open();
                string query = "SELECT COUNT(*) FROM Nodes WHERE NodeNumber = @num";
                if (excludeNodeId.HasValue) query += " AND Id != @excludeId";
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@num", nodeNumber);
                    if (excludeNodeId.HasValue) cmd.Parameters.AddWithValue("@excludeId", excludeNodeId.Value);
                    return (int)cmd.ExecuteScalar() > 0;
                }
            }
        }

        public static bool LocalIPExists(string ip, int? excludeNodeId = null)
        {
            using (var conn = new SqlConnection(DatabaseHelper.ConnectionString))
            {
                conn.Open();
                string query = "SELECT COUNT(*) FROM Nodes WHERE LocalIPAddress = @ip";
                if (excludeNodeId.HasValue) query += " AND Id != @excludeId";
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@ip", ip);
                    if (excludeNodeId.HasValue) cmd.Parameters.AddWithValue("@excludeId", excludeNodeId.Value);
                    return (int)cmd.ExecuteScalar() > 0;
                }
            }
        }

        // ------------------ Method is used in panelSettings.
        public static List<PanelSettingsModel> GetUncalibratedNodesForRoute(int route)
        {
            var list = new List<PanelSettingsModel>();
            using (var conn = new SqlConnection(DatabaseHelper.ConnectionString))
            {
                conn.Open();
                string query = @"
            SELECT A.Route, A.PanelLocation, A.PanelSerialNumber,
                   N.LocalIPAddress, A.Description
            FROM Areas A
            INNER JOIN Nodes N ON A.PanelSerialNumber = N.PanelSerialNumber
            WHERE A.Route = @Route AND N.Calibration = 0
            ORDER BY A.PanelLocation ASC";
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@Route", route);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            list.Add(new PanelSettingsModel
                            {
                                Route = reader.GetInt32(0),
                                PanelLocation = reader.GetInt32(1),
                                PanelSerialNumber = reader.GetInt32(2),
                                IPAddress = reader.GetString(3),
                                Description = reader.GetString(4)
                            });
                        }
                    }
                }
            }
            return list;
        }

        // ------------------ The next four methods are used in PanelSettings. 
        public static bool IpAddressExists(string ipAddress, int? excludeNodeId = null)
        {
            using (var connection = new SqlConnection(DatabaseHelper.ConnectionString))
            {
                connection.Open();
                string query = "SELECT COUNT(*) FROM Nodes WHERE IPAddress = @IPAddress";
                if (excludeNodeId.HasValue)
                    query += " AND Id != @Id";

                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@IPAddress", ipAddress);
                    if (excludeNodeId.HasValue)
                        command.Parameters.AddWithValue("@Id", excludeNodeId.Value);

                    int count = (int)command.ExecuteScalar();
                    return count > 0;
                }
            }
        }

        public static bool PanelSerialNumberExists(int panelSerialNumber, int? excludeNodeId = null)
        {
            using (var connection = new SqlConnection(DatabaseHelper.ConnectionString))
            {
                connection.Open();
                string query = "SELECT COUNT(*) FROM Nodes WHERE PanelSerialNumber = @panelSerialNumber";
                if (excludeNodeId.HasValue)
                    query += " AND Id != @Id";

                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@panelSerialNumber", panelSerialNumber);
                    if (excludeNodeId.HasValue)
                        command.Parameters.AddWithValue("@Id", excludeNodeId.Value);

                    int count = (int)command.ExecuteScalar();
                    return count > 0;
                }
            }
        }

        public static bool PanelLocationExistsForRoute(int route, int panelLocation, int? excludeNodeId = null)
        {
            using (var connection = new SqlConnection(DatabaseHelper.ConnectionString))
            {
                connection.Open();
                string query = "SELECT COUNT(*) FROM Nodes WHERE Route = @Route AND PanelLocation = @PanelLocation";
                if (excludeNodeId.HasValue)
                    query += " AND Id != @Id";

                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@Route", route);
                    command.Parameters.AddWithValue("@PanelLocation", panelLocation);
                    if (excludeNodeId.HasValue)
                        command.Parameters.AddWithValue("@Id", excludeNodeId.Value);

                    int count = (int)command.ExecuteScalar();
                    return count > 0;
                }
            }
        }

        public static void MarkCalibrated(int panelSerialNumber)
        {
            using (var conn = new SqlConnection(DatabaseHelper.ConnectionString))
            {
                conn.Open();
                string query = @"UPDATE Nodes 
                         SET Calibration = 1 
                         WHERE PanelSerialNumber = @serial";
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@serial", panelSerialNumber);
                    cmd.ExecuteNonQuery();
                }
            }
        }
    }
}