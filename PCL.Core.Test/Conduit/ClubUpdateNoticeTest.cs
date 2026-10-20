using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Conduit;

namespace PCL.Core.Test.Conduit;

[TestClass]
public sealed class ClubUpdateNoticeTest
{
    [TestMethod]
    public void ParsesArticleOnlySortsDatesAndDeduplicatesSameDate()
    {
        const string html = """
            <nav>
              <a href="/updates/2026-01-01/">导航不属于正文</a>
            </nav>
            <article>
              <h1>更新日志</h1>
              <ul>
                <li><a href="/updates/2026-08-18/"><span>2026.08.18</span> · <strong>SHOU 服务器更新公告</strong></a></li>
                <li><a href="https://conduit-club.github.io/updates/2026-09-28/"><span>2026.09.28</span> · 生存服务器新增插件</a></li>
                <li><a href="/updates/2026-09-28/">2026.09.28 · 同日的装饰性改写</a></li>
                <li><a href="/updates/2026-07-12/">2026.07.12 · 更早记录</a></li>
                <li><a href="/updates/2026-10-03/">2026.10.03 · 尚未发布</a></li>
              </ul>
            </article>
            """;

        var notices = ClubUpdateNoticeParser.Parse(html, new DateOnly(2026, 10, 2));

        Assert.AreEqual(3, notices.Count);
        Assert.AreEqual(new DateOnly(2026, 9, 28), notices[0].Date);
        Assert.AreEqual("生存服务器新增插件", notices[0].Title);
        Assert.AreEqual(new DateOnly(2026, 8, 18), notices[1].Date);
        Assert.AreEqual(new DateOnly(2026, 7, 12), notices[2].Date);
        Assert.AreEqual("https://conduit-club.github.io/updates/2026-09-28/", notices[0].Url.ToString());
    }

    [TestMethod]
    public void MalformedOrMissingArticleProducesNoNotice()
    {
        Assert.AreEqual(0, ClubUpdateNoticeParser.Parse("<main><a href=\"/updates/2026-09-28/\">孤立链接</a></main>").Count);
        Assert.AreEqual(0, ClubUpdateNoticeParser.Parse("<article><a href=\"/updates/2026-09-31/\">不存在的日期</a></article>").Count);
        Assert.IsNull(ClubUpdateNoticeParser.ParseLatest("<article><p>没有日期文章</p></article>", new DateOnly(2026, 10, 2)));
    }

    [TestMethod]
    [DataRow("https://conduit-club.github.io/updates/2026-09-28/", true)]
    [DataRow("/updates/2026-09-28/", true)]
    [DataRow("https://conduit-club.github.io:444/updates/2026-09-28/", false)]
    [DataRow("http://conduit-club.github.io/updates/2026-09-28/", false)]
    [DataRow("https://conduit-club.github.io.attacker.test/updates/2026-09-28/", false)]
    [DataRow("https://attacker.test/updates/2026-09-28/", false)]
    [DataRow("https://conduit-club.github.io/updates/2026-09-28/?next=https://attacker.test/", false)]
    [DataRow("//attacker.test/updates/2026-09-28/", false)]
    public void ArticleLinksRequireTrustedHttpsOriginAndDatePath(string href, bool expected)
        => Assert.AreEqual(expected, ClubUpdateNoticeParser.TryGetTrustedArticle(href, out _, out _));

    [TestMethod]
    public void NotificationOnlyAdvancesForStrictlyNewerDate()
    {
        var notice = ClubUpdateNoticeParser.ParseLatest(
            "<article><a href=\"/updates/2026-09-28/\">2026.09.28 · 最新</a></article>",
            new DateOnly(2026, 10, 2))!;

        Assert.IsTrue(ClubUpdateNoticeParser.ShouldNotify(notice, null));
        Assert.IsFalse(ClubUpdateNoticeParser.ShouldNotify(notice, new DateOnly(2026, 9, 28)));
        Assert.IsFalse(ClubUpdateNoticeParser.ShouldNotify(notice, new DateOnly(2026, 10, 1)));
        Assert.IsTrue(ClubUpdateNoticeParser.ShouldNotify(notice, new DateOnly(2026, 9, 27)));
    }
}
