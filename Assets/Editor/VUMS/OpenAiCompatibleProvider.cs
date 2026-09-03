using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace VUMS.Editor
{
    [Serializable]
    internal sealed class OpenAiChatResult
    {
        public bool Success;
        public string Content;
        public string Error;
        public int PromptTokens;
        public int CompletionTokens;
    }

    /// <summary>
    /// OpenAI Chat Completions 兼容协议的 HTTP 客户端。覆盖 OpenAI 官方、skyunion relay
    /// 中转、自建 / Ollama / vLLM 等任意 OpenAI 兼容服务。使用 UnityWebRequest，无外部依赖。
    /// 仅做单轮 system + user → answer，不支持流式、工具调用、图片输入。
    /// 注意：编辑器引用程序集中的 UnityWebRequestAsyncOperation 不实现 GetAwaiter，
    /// 因此用 EditorApplication.update 轮询完成，再用 TaskCompletionSource 暴露 Task。
    /// </summary>
    internal static class OpenAiCompatibleProvider
    {
        #pragma warning disable CS0649
        [Serializable]
        private sealed class ChatRequest
        {
            public string model;
            public ChatMessage[] messages;
            public int max_tokens;
            public float temperature;
        }

        [Serializable]
        private sealed class ChatMessage
        {
            public string role;
            public string content;
        }

        [Serializable]
        private sealed class ChatResponse
        {
            public Choice[] choices;
            public Usage usage;
            public ErrorInfo error;
        }

        [Serializable]
        private sealed class Choice
        {
            public Message message;
        }

        [Serializable]
        private sealed class Message
        {
            public string content;
        }

        [Serializable]
        private sealed class Usage
        {
            public int prompt_tokens;
            public int completion_tokens;
        }

        [Serializable]
        private sealed class ErrorInfo
        {
            public string message;
        }
        #pragma warning restore CS0649

        public static Task<OpenAiChatResult> ChatAsync(
            string baseUrl, string apiKey, string model,
            string system, string user, int maxTokens, int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<OpenAiChatResult>();
            var endpoint = baseUrl.TrimEnd('/') + "/chat/completions";

            var request = new ChatRequest
            {
                model = model,
                max_tokens = Mathf.Max(1, maxTokens),
                temperature = 0.3f,
                messages = new[]
                {
                    new ChatMessage { role = "system", content = system },
                    new ChatMessage { role = "user", content = user },
                },
            };

            var json = JsonUtility.ToJson(request);
            var body = Encoding.UTF8.GetBytes(json);

            var webRequest = new UnityWebRequest(endpoint, "POST")
            {
                uploadHandler = new UploadHandlerRaw(body),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = Mathf.Clamp(timeoutSeconds, 5, 600),
            };
            webRequest.SetRequestHeader("Content-Type", "application/json");
            webRequest.SetRequestHeader("Authorization", "Bearer " + apiKey);

            EditorApplication.update += Poll;

            void Poll()
            {
                if (!webRequest.isDone)
                    return;
                EditorApplication.update -= Poll;

                OpenAiChatResult result;
                if (webRequest.result != UnityWebRequest.Result.Success)
                {
                    var err = ExtractErrorMessage(webRequest.downloadHandler?.text);
                    result = new OpenAiChatResult
                    {
                        Success = false,
                        Error = string.IsNullOrEmpty(err)
                            ? $"HTTP {(int)webRequest.responseCode}: {webRequest.error}"
                            : err,
                    };
                }
                else
                {
                    result = ParseResponse(webRequest.downloadHandler?.text ?? string.Empty);
                }

                webRequest.Dispose();
                tcs.TrySetResult(result);
            }

            try
            {
                webRequest.SendWebRequest();
            }
            catch (Exception exception)
            {
                EditorApplication.update -= Poll;
                webRequest.Dispose();
                tcs.TrySetResult(new OpenAiChatResult { Success = false, Error = exception.Message });
            }

            return tcs.Task;
        }

        private static string ExtractErrorMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;
            try
            {
                var err = JsonUtility.FromJson<ChatResponse>(text);
                return err?.error?.message ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static OpenAiChatResult ParseResponse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new OpenAiChatResult { Success = false, Error = "空响应" };
            try
            {
                var resp = JsonUtility.FromJson<ChatResponse>(text);
                if (resp?.choices == null || resp.choices.Length == 0)
                    return new OpenAiChatResult { Success = false, Error = "响应缺少 choices" };
                var content = resp.choices[0]?.message?.content ?? string.Empty;
                return new OpenAiChatResult
                {
                    Success = true,
                    Content = content,
                    PromptTokens = resp.usage?.prompt_tokens ?? 0,
                    CompletionTokens = resp.usage?.completion_tokens ?? 0,
                };
            }
            catch (Exception exception)
            {
                return new OpenAiChatResult { Success = false, Error = "解析响应失败: " + exception.Message };
            }
        }
    }
}
