using Microsoft.Data.SqlClient;
using System.Collections.Generic;

namespace CompanyUtilityApp.ProgramFiles
{
    public class Area
    {
        public int Id { get; set; }
        public int Route { get; set; }
        public int PanelLocation { get; set; }
        public int HoldingRegister { get; set; }
        public int PanelSerialNumber { get; set; }
        public string Description { get; set; }
    }

    public static class AreaRepository
    {
        // Retrieve all areas for a given route, ordered by PanelLocation
        public static List<Area> GetAreasByRoute(int route)
        {
            var list = new List<Area>();
            using (var conn = new SqlConnection(DatabaseHelper.ConnectionString))
            {
                conn.Open();
                string query = @"SELECT Id, Route, PanelLocation, HoldingRegister, 
                                        PanelSerialNumber, Description
                                 FROM Areas
                                 WHERE Route = @Route
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

        public static Area GetAreaByPanelSerialNumber(int serial)
        {
            using (var conn = new SqlConnection(DatabaseHelper.ConnectionString))
            {
                conn.Open();
                string query = "SELECT Id, Route, PanelLocation, HoldingRegister, PanelSerialNumber, Description FROM Areas WHERE PanelSerialNumber = @serial";
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@serial", serial);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            return new Area
                            {
                                Id = reader.GetInt32(0),
                                Route = reader.GetInt32(1),
                                PanelLocation = reader.GetInt32(2),
                                HoldingRegister = reader.GetInt32(3),
                                PanelSerialNumber = reader.GetInt32(4),
                                Description = reader.IsDBNull(5) ? null : reader.GetString(5)
                            };
                        }
                    }
                }
            }
            return null;
        }

        // Update PanelSerialNumber and Description for a given area ID.
        // Returns true if the update succeeded, false if the new serial number is already in use.
        public static bool UpdateArea(int id, int newPanelSerialNumber, string newDescription)
        {
            // Check uniqueness of PanelSerialNumber (exclude current record)
            if (PanelSerialNumberExists(newPanelSerialNumber, id))
                return false;

            using (var conn = new SqlConnection(DatabaseHelper.ConnectionString))
            {
                conn.Open();
                string query = @"UPDATE Areas 
                                 SET PanelSerialNumber = @Serial, Description = @Desc
                                 WHERE Id = @Id";
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@Serial", newPanelSerialNumber);
                    cmd.Parameters.AddWithValue("@Desc", (object)newDescription ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Id", id);
                    cmd.ExecuteNonQuery();
                }
            }
            return true;
        }

        // Check if a PanelSerialNumber already exists (optionally excluding a specific area ID)
        public static bool PanelSerialNumberExists(int serial, int? excludeId = null)
        {
            using (var conn = new SqlConnection(DatabaseHelper.ConnectionString))
            {
                conn.Open();
                string query = "SELECT COUNT(*) FROM Areas WHERE PanelSerialNumber = @Serial";
                if (excludeId.HasValue)
                    query += " AND Id != @ExcludeId";
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@Serial", serial);
                    if (excludeId.HasValue)
                        cmd.Parameters.AddWithValue("@ExcludeId", excludeId.Value);
                    int count = (int)cmd.ExecuteScalar();
                    return count > 0;
                }
            }
        }
    }
}