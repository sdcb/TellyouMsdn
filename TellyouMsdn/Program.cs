using Dapper;
using HtmlAgilityPack;
using HtmlAgilityPack.CssSelectors.NetCore;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OfficeOpenXml;
using sdmap.Extensions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace TellyouMsdn
{
    class Program
    {
        static HttpClient Http = new HttpClient();

        static void Main(string[] args)
        {
            SdmapExtensions.SetSqlDirectory("sqls");
            Console.OutputEncoding = Encoding.Unicode;
            Http.DefaultRequestHeaders.Add("Referer", "http://msdn.itellyou.cn/");
            Http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.8,zh-CN;q=0.6,zh;q=0.4");
            Http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/57.0.2987.133 Safari/537.36");
            Http.DefaultRequestHeaders.Add("Cookie", "__cfduid=dfece6ce06f3ab9d3dc691a6f82082bc41491983598; preurl=/; captchaKey=b867d8b6b8; captchaExpire=1491984191; cookietest=1; __utmt=1; __utma=86709124.2064828452.1491983600.1491983600.1491983600.1; __utmb=86709124.1.10.1491983600; __utmc=86709124; __utmz=86709124.1491983600.1.1.utmcsr=(direct)|utmccn=(direct)|utmcmd=(none); UM_distinctid=15b612709404a3-0b66107155c198-8373f6a-1fa400-15b61270941e29; CNZZDATA1605814=cnzz_eid%3D427129345-1491981796-http%253A%252F%252Fmsdn.itellyou.cn%252F%26ntime%3D1491981796; Hm_lvt_8688ca4bc18cbc647c9c68fdaef6bc24=1491807607,1491809273,1491817266,1491978000; Hm_lpvt_8688ca4bc18cbc647c9c68fdaef6bc24=1491983600");

            //DoWork().Wait();
            //CompleteItems();
            ExportToExcel();
        }

        private static void ExportToExcel()
        {
            using (var connection = GetConnection())
            {
                var view = connection
                    .QueryById("View")
                    .Select(x => (IDictionary<string, object>)x)
                    .AsList();
                using (var excel = new ExcelPackage())
                {
                    var sheet = excel.Workbook.Worksheets.Add("MSDN");
                    var header = view[0].Keys.ToList();
                    for (var col = 1; col <= header.Count; ++col)
                    {
                        sheet.Cells[1, col].Value = header[col - 1];
                        sheet.Cells[1, col].Style.Font.Bold = true;
                    }

                    for (var row = 2; row < view.Count + 2; ++row)
                    {
                        var col = 1;
                        foreach (var kv in view[row - 2])
                        {
                            sheet.Cells[row, col].Value = kv.Value;
                            ++col;
                        }
                    }

                    for (var col = 1; col <= header.Count; ++col)
                    {
                        sheet.Column(col).AutoFit();
                    }

                    excel.SaveAs(new FileInfo("MSDN.xlsx"));
                }
            }
        }

        private static SqliteConnection GetConnection()
        {
            return new SqliteConnection("Data Source=data.db");
        }

        private static void CompleteItems()
        {
            using (var connection = GetConnection())
            {
                var items = connection
                    .QueryById<Item>("GetItem")
                    .AsList();

                var times = 0;
                items
                    .AsParallel()
                    .Select(x => GetItemDetails(x.Id).Result)
                    .ForAll(x =>
                    {
                        lock (connection)
                        {
                            ++times;
                            Console.WriteLine($"{times}/{items.Count}");
                            connection.ExecuteById("SetItemDetails", x);
                        }
                    });
            }
        }

        static async Task DoWork()
        {
            using (var connection = new SqliteConnection("Data Source=data.db"))
            {
                connection.ExecuteById("CreateAll");

                var categories = await GetCategories();
                connection.ExecuteById("AddCategory", categories);

                foreach (var category in categories)
                {
                    Console.WriteLine($"{category.Name}...");

                    var products = await GetProducts(category.Id);
                    connection.ExecuteById("AddProduct", products);

                    foreach (var product in products)
                    {
                        var languages = await GetLanguages(product.Id);
                        connection.ExecuteById("AddLanguage", languages);

                        foreach (var language in languages)
                        {
                            var items = await GetItems(language.Id, product.Id);
                            connection.ExecuteById("AddItem", items);
                        }
                    }
                }
            }
        }

        static async Task<ItemDetails> GetItemDetails(string itemId)
        {
            var response = await Http.PostAsync(
                "http://msdn.itellyou.cn/Category/GetProduct",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["id"] = itemId
                }));
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            var token = JToken.Parse(json);
            if (!token["status"].Value<bool>())
                throw new Exception("请求失败。");

            return new ItemDetails
            {
                FileName = token["result"]["FileName"].Value<string>(),
                SHA1 = token["result"]["SHA1"].Value<string>(),
                Size = token["result"]["size"].Value<string>(),
                Id = itemId
            };
        }

        static async Task<List<Item>> GetItems(string languageId, string productId)
        {
            var response = await Http.PostAsync(
                "http://msdn.itellyou.cn/Category/GetList",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["id"] = productId,
                    ["lang"] = languageId,
                    ["filter"] = "true"
                }));
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            var token = JToken.Parse(json);
            if (!token["status"].Value<bool>())
                throw new Exception("请求失败。");

            return token["result"].Select(x => new Item
            {
                Id = x["id"].Value<string>(),
                Name = x["name"].Value<string>(),
                Time = x["post"].Value<DateTime>(),
                Url = x["url"].Value<string>(),
                LanguageId = languageId,
                ProductId = productId
            })
            .ToList();
        }

        static async Task<List<Language>> GetLanguages(string productId)
        {
            var response = await Http.PostAsync(
                "http://msdn.itellyou.cn/Category/GetLang",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["id"] = productId
                }));
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            var token = JToken.Parse(json);
            if (!token["status"].Value<bool>())
                throw new Exception("请求失败。");

            return token["result"].Select(x => new Language
            {
                Id = x["id"].Value<string>(),
                Name = x["lang"].Value<string>()
            })
            .ToList();
        }

        static async Task<List<Product>> GetProducts(string categoryId)
        {
            var response = await Http.PostAsync(
                "http://msdn.itellyou.cn/Category/Index",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["id"] = categoryId
                }));
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            return JsonConvert
                .DeserializeObject<IEnumerable<Category>>(json)
                .Select(x => new Product
                {
                    Id = x.Id,
                    CategoryId = categoryId,
                    Name = x.Name
                })
                .ToList();
        }

        static async Task<List<Category>> GetCategories()
        {
            var response = await Http.GetAsync("http://msdn.itellyou.cn");
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync();

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var aTags = doc.QuerySelectorAll("#accordion > div > div.panel-heading > h4 > a");
            return aTags.Select(x =>
            {
                return new Category
                {
                    Id = x.Attributes["data-menuid"].Value,
                    Name = x.InnerHtml
                };
            }).ToList();
        }
    }

    internal class Item
    {
        public string Id { get; set; }

        public string ProductId { get; set; }

        public string LanguageId { get; set; }

        public string Name { get; set; }

        public DateTime Time { get; set; }

        public string Url { get; set; }
    }

    internal class ItemDetails
    {
        public string Id { get; set; }

        public string Size { get; set; }

        public string FileName { get; set; }

        public string SHA1 { get; set; }
    }

    internal class Language
    {
        public string Id { get; set; }

        public string Name { get; set; }
    }

    internal class Category
    {
        public string Id { get; set; }

        public string Name { get; set; }
    }

    internal class Product
    {
        public string Id { get; set; }
        public string CategoryId { get; set; }
        public string Name { get; set; }
    }
}