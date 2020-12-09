using System;
using System.Net;
using System.Net.Http;
using System.Collections.Specialized;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using HtmlAgilityPack;

namespace Bin4.Function
{
    public static class bin4monthly
    {
        private const TEXTBELT_SECRETKEY = "";
        [FunctionName("bin4monthly")]
        public static void Run([TimerTrigger("0 0 9 1 * *")]TimerInfo myTimer, ILogger log)
        {
            string[] recipientList = {"2502086037", "2502086822"};
            try
            {
                var bin4burger = GetBin4MonthlyFeature().Result;
                foreach(string recipient in recipientList)
                {
                    log.LogInformation(SendSMS(recipient, bin4burger));
                    System.Threading.Thread.Sleep(2000);
                }
            }
            catch (Exception e) 
            {
                log.LogInformation(e.ToString());
                SendSMS("2502086037", e.ToString());
            }
        }
        public static async Task<string> GetBin4MonthlyFeature() {
            using (HttpClient HttpClient = new HttpClient()){
                string menuPage = "http://www.bin4burgerlounge.com/our-saanich-menu/";
                string html = await HttpClient.GetStringAsync(menuPage);
            
                HtmlDocument doc = new HtmlDocument();
                doc.LoadHtml(html); 

                string burgerName = doc.DocumentNode
                    .SelectSingleNode("//*[@id='main']/section/div[2]/div/div[1]/div/div[2]/div[2]/h4/div/text()")
                    .InnerHtml.Trim();
                
                burgerName = burgerName.Split('\u2013')[1];
                burgerName = burgerName.Replace(@"&#8220;","");
                burgerName = burgerName.Replace(@"&#8221;","").Trim();

                string burgerDescription = doc.DocumentNode
                    .SelectSingleNode("//*[@id='main']/section/div[2]/div/div[1]/div/div[2]/div[2]/p/text()")
                    .InnerHtml.Trim();

                string burgerPrice = doc.DocumentNode
                    .SelectSingleNode("//*[@id='main']/section/div[2]/div/div[1]/div/div[2]/div[2]/h4/span/text()")
                    .InnerHtml.Trim();

                return $"Bin4 Feature Burger:\n{burgerName} - {burgerDescription} - {burgerPrice}";
            }
        }

        public static string SendSMS(string recipient, string message){
            using(WebClient client = new WebClient())
            { 
                byte[] response = client.UploadValues("http://textbelt.com/text", new NameValueCollection() {
                    { "phone", recipient },
                    { "message", message },
                    //{ "key", TEXTBELT_SECRETKEY},
                    { "key", "textbelt" },
                });
                return $"{recipient} -> '{message}'";
            }
        }
    }
}
