using System.Text;
using AIRadio.Infrastructure;

namespace AIRadio.Infrastructure.Tests;

public class DpapiTokenStoreTests
{
    [Fact]
    public void SaveLoadDelete_RoundTrips_AndEncryptsAtRest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"airadio-test-{Guid.NewGuid():N}.token");
        var store = new DpapiTokenStore(path);
        try
        {
            Assert.Null(store.Load());

            store.Save("REFRESH-XYZ");
            Assert.Equal("REFRESH-XYZ", store.Load());

            // 保存ファイルは暗号化されている（平文がそのまま残らない）
            var raw = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            Assert.DoesNotContain("REFRESH-XYZ", raw);

            store.Delete();
            Assert.Null(store.Load());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
