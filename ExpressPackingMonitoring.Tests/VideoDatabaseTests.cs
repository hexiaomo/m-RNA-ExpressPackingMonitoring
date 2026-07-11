using ExpressPackingMonitoring.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class VideoDatabaseTests
{
    [Fact]
    public void GetRecentCompletedVideos_ReturnsLatestTenValidRecordsForDate()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"ExpressPackingMonitoringTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string databasePath = Path.Combine(tempDirectory, "videos.db");
            DateTime date = new(2026, 7, 11);
            using (var database = new VideoDatabase(databasePath))
            {
                AddCompleted(database, "YESTERDAY", "发货", Path.Combine(tempDirectory, "yesterday.mp4"), date.AddDays(-1).AddHours(23));

                for (int index = 0; index < 22; index++)
                {
                    AddCompleted(
                        database,
                        $"TODAY-{index:00}",
                        index % 2 == 0 ? "发货" : "退货",
                        Path.Combine(tempDirectory, $"today-{index:00}.mp4"),
                        date.AddHours(8).AddMinutes(index));
                }

                string deletedPath = Path.Combine(tempDirectory, "deleted.mp4");
                AddCompleted(database, "DELETED", "发货", deletedPath, date.AddHours(22));
                database.MarkVideoDeleted(deletedPath, "测试清理");
                database.InsertVideoRecord("INCOMPLETE", "退货", "", "", Path.Combine(tempDirectory, "incomplete.mp4"), date.AddHours(23));

                List<VideoRecord> records = database.GetRecentCompletedVideos(date, 20);

                Assert.Equal(20, records.Count);
                Assert.Equal("TODAY-21", records[0].OrderId);
                Assert.Equal("TODAY-02", records[^1].OrderId);
                Assert.Equal("退货", records[0].Mode);
                Assert.DoesNotContain(records, record => record.OrderId is "YESTERDAY" or "DELETED" or "INCOMPLETE");
                Assert.True(records.SequenceEqual(records.OrderByDescending(record => record.StartTime)));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static void AddCompleted(VideoDatabase database, string orderId, string mode, string path, DateTime startTime)
    {
        long id = database.InsertVideoRecord(orderId, mode, "", "", path, startTime);
        database.UpdateVideoRecordOnStop(id, startTime.AddMinutes(1), 60, 1024, "手动");
    }
}
