using System;
using System.IO;
using System.Text;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace VoiceRecognitionBot
{
    public class TelegramBot : IDisposable
    {
        private readonly TelegramBotClient _botClient;
        private readonly IConfigurationRoot _configuration;
        private readonly HttpClient _httpClient;
        private readonly string _lang;
        private readonly int _resultType;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly string _apiKeyId;
        private readonly string _apiKeySecret;


        public TelegramBot(IConfiguration configuration)
        {
            _configuration = (IConfigurationRoot)configuration;
            _botClient = new TelegramBotClient(_configuration["TelegramApiKey"]);
            _httpClient = new HttpClient();
            _lang = _configuration["Speechflow:LanguageCode"] ?? "ru";
            _resultType = 1;
            _cancellationTokenSource = new CancellationTokenSource();

            // Set up Speechflow API credentials
            _apiKeyId = _configuration["Speechflow:KeyId"];
            _apiKeySecret = _configuration["Speechflow:KeySecret"];

            // Configure HttpClient headers
            _httpClient.DefaultRequestHeaders.Add("keyId", _apiKeyId);
            _httpClient.DefaultRequestHeaders.Add("keySecret", _apiKeySecret);
        }

        public async Task StartAsync()
        {
            ReceiverOptions receiverOptions = new()
            {
                AllowedUpdates = Array.Empty<UpdateType>()
            };

            _botClient.StartReceiving(HandleUpdateAsync, HandlePollingErrorAsync, receiverOptions, _cancellationTokenSource.Token);

            var me = await _botClient.GetMeAsync();
            Console.WriteLine($"Start listening for @{me.Username}");
        }


        private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            if (update.Message.Type == MessageType.Voice)
            {
                Message voiceMessage = update.Message;
                Console.WriteLine($"Received voice message from {voiceMessage.From.Username}");

                string fileId = voiceMessage.Voice.FileId;
                Telegram.Bot.Types.File file = await _botClient.GetFileAsync(fileId);
                using FileStream tempFileStream = System.IO.File.Create("tempVoiceMessage.ogg");
                await _botClient.DownloadFileAsync(file.FilePath, tempFileStream);
                tempFileStream.Seek(0, SeekOrigin.Begin);

                string recognizedText = await RecognizeSpeechAsync(tempFileStream);
                await _botClient.SendTextMessageAsync(voiceMessage.Chat.Id, recognizedText);
            }
        }

        private Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            var errorMessage = exception switch
            {
                ApiRequestException apiRequestException => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
                _ => exception.ToString()
            };

            Console.WriteLine(errorMessage);
            return Task.CompletedTask;
        }

        public void Stop()
        {
            _cancellationTokenSource.Cancel();
        }

        private async Task<string> RecognizeSpeechAsync(Stream audioStream)
        {
            MultipartFormDataContent createData = new MultipartFormDataContent();
            createData.Add(new StringContent(_lang), "lang");
            createData.Add(new StreamContent(audioStream), "file", "voice_message.ogg");

            string createUrl = "https://api.speechflow.io/asr/file/v1/create";
            HttpResponseMessage response = await _httpClient.PostAsync(createUrl, createData);

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Speechflow API request failed: {response.StatusCode}");
            }

            string createResult = await response.Content.ReadAsStringAsync();
            dynamic createResponse = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(createResult);
            string taskId = createResponse.taskId.ToString();


            // Poll for transcription result
            string queryUrl = $"https://api.speechflow.io/asr/file/v1/query?taskId={taskId}&resultType={_resultType}";
            while (true)
            {
                HttpResponseMessage queryResponse = await _httpClient.GetAsync(queryUrl);
                queryResponse.EnsureSuccessStatusCode();

                string queryResult = await queryResponse.Content.ReadAsStringAsync();
                dynamic queryJSON = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(queryResult);

                if (queryJSON.code == 11000)
                {
                    ResponseResult responseResult = Newtonsoft.Json.JsonConvert.DeserializeObject<ResponseResult>(queryJSON.result.ToString());
                    StringBuilder concatenatedText = new StringBuilder();
                    foreach (Sentence sentence in responseResult.sentences)
                    {
                        concatenatedText.Append(sentence.s);
                        concatenatedText.Append(" ");
                    }
                    return concatenatedText.ToString();
                }
                else if (queryJSON.code == 11001)
                {
                    await Task.Delay(3000); // Wait and then query again
                }
                else
                {
                    throw new Exception($"Speechflow API query failed: {queryJSON.msg}");
                }
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
            _cancellationTokenSource?.Dispose();
        }
    }

    public class Program
    {
        static async Task Main(string[] args)
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            using TelegramBot bot = new TelegramBot(configuration);
            await bot.StartAsync();

            Console.ReadLine();

            bot.Stop();
        }
    }
}