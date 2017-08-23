using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Configuration;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http;
using System.Diagnostics;

namespace JustLogIt.Nuget.Library {
  public class Logger {

    private static Thread _processingThread = null;
    private static ConcurrentQueue<JustLogIt.Nuget.Library.QueueItem> _queue = new ConcurrentQueue<JustLogIt.Nuget.Library.QueueItem>();
    private static bool _stopping = false;
    private static string _loggingToken = "";

    private static List<int> _executionTimes = new List<int>();
    private static Queue<Exception> _exceptionQueue = new Queue<Exception>();


    public static void LogError(Exception exception, string Detail = null, string UserIdentifier = null, Dictionary<string, string> details = null) {
      try {
        List<string> values = new List<string>();
        values.Add("\"message\":\"" + EscapeString(exception.Message) + "\"");
        values.Add("\"stack\":\"" + EscapeString(exception.StackTrace) + "\"");
        values.Add("\"details\":\"" + EscapeString(Detail) + "\"");

        if (null != details) {
          foreach (string key in details.Keys) {
            values.Add("\"" + key.Replace("\"", "") + "\":\"" + EscapeString(details[key]) + "\"");
          }
        }

        string output = "{" + String.Join(",", values) + "}";
        _queue.Enqueue(new JustLogIt.Nuget.Library.QueueItem() {
          Method = "error",
          Details = output
        });

        StartProcessing();
      }
      catch (Exception) {
      }
    }

    public static void LogPerformance(long Milliseconds, string Method, string UserIdentifier = null, Dictionary<string, string> details = null) {
      try {
        string queryString = "t=" + Milliseconds.ToString() + "&m=" + WebUtility.HtmlEncode(Method);
        if (!String.IsNullOrEmpty(UserIdentifier)) queryString += "&u=" + WebUtility.HtmlEncode(UserIdentifier);
        if (null != details) {
          foreach (string key in details.Keys) {
            queryString += "&" + WebUtility.HtmlEncode(key) + "=" + WebUtility.HtmlEncode(details[key]);
          }
        }

        _queue.Enqueue(new JustLogIt.Nuget.Library.QueueItem() {
          Method = "perf",
          Details = queryString
        });

        StartProcessing();
      }
      catch (Exception) {

      }
    }

    public static void LogEvent(string Name, string UserIdentifier = null, Dictionary<string, string> details = null) {
      try {
        string queryString = "n=" + WebUtility.HtmlEncode(Name);
        if (!String.IsNullOrEmpty(UserIdentifier)) queryString += "&u=" + WebUtility.HtmlEncode(UserIdentifier);
        if (null != details) {
          foreach (string key in details.Keys) {
            queryString += "&" + WebUtility.HtmlEncode(key) + "=" + WebUtility.HtmlEncode(details[key]);
          }
        }

        _queue.Enqueue(new JustLogIt.Nuget.Library.QueueItem() {
          Method = "event",
          Details = queryString
        });

        StartProcessing();
      }
      catch (Exception) {

      }
    }

    public static void LogInformation(string Method, string Detail, string UserIdentifier = null, Dictionary<string, string> details = null) {
      try {
        string queryString = "m=" + WebUtility.HtmlEncode(Method) + "&d=" + WebUtility.HtmlEncode(Detail);
        if (!String.IsNullOrEmpty(UserIdentifier)) queryString += "&u=" + WebUtility.HtmlEncode(UserIdentifier);
        if (null != details) {
          foreach (string key in details.Keys) {
            queryString += "&" + WebUtility.HtmlEncode(key) + "=" + WebUtility.HtmlEncode(details[key]);
          }
        }

        _queue.Enqueue(new JustLogIt.Nuget.Library.QueueItem() {
          Method = "info",
          Details = queryString
        });

        StartProcessing();
      }
      catch (Exception) {

      }
    }


    public static void StopProcessing() {
      try {
        _executionTimes.Clear();
        _stopping = true;
      }
      catch (Exception) { }
    }


    public static Dictionary<string, double> GetExecutionStats() {
      int[] times;
      lock(typeof(Logger)) {
        times =_executionTimes.ToArray();
        _executionTimes.Clear();
      }

      int total = 0;
      int max = 0;
      int min = 100000;

      foreach(int value in times) {
        total += value;
        if (value > max) max = value;
        if (value < min) min = value;
      }
      
      double avg = (total / times.Length);

      Dictionary<string, double> stats = new Dictionary<string, double>();
      stats.Add("Min", min);
      stats.Add("Max", max);
      stats.Add("Avg", avg);

      return stats;
    }


    public static int GetQueueCount() {
      return _queue.Count;
    }

    public static string GetQueueErrors() {
      string result = "";

      Exception e = _exceptionQueue.Dequeue();
      while (e != null) {
        result += e.ToString() + Environment.NewLine + Environment.NewLine;
        e = _exceptionQueue.Dequeue();
      }
      return result;
    }


    private static void StartProcessing() {

      try {
        // get the jli token
        _loggingToken = ConfigurationManager.AppSettings["JustLogItToken"];
        if (String.IsNullOrEmpty(_loggingToken)) {
          throw new ApplicationException("No logging token specified");
        }

        if (null == _processingThread) {
          _processingThread = new Thread(new ThreadStart(ProcessQueue));
          _processingThread.IsBackground = true;
          _processingThread.Start();
        }
        else if (!_processingThread.IsAlive) {
          _processingThread.Abort();
          _processingThread = null;
          StartProcessing();
        }
      }
      catch (Exception) { }
    }


    private static void ProcessQueue() {

      try {
        string loggingUrl = "https://addto.justlog.it/v1/log/" + _loggingToken + "/{0}";

        while (!_stopping) {
          Stopwatch sw = new Stopwatch();
          using (HttpClient hc = new HttpClient()) {
            JustLogIt.Nuget.Library.QueueItem value;
            while (_queue.TryDequeue(out value)) {
              if (!_stopping) {
                sw.Start();
                string url = String.Format(loggingUrl, value.Method);
                try {
                  HttpResponseMessage response = null;
                  if (value.Method == "error") {
                    response = hc.PostAsync(url, new StringContent(value.Details, Encoding.UTF8, "application/json")).Result;
                  }
                  else {
                    url += "?" + value.Details;
                    response = hc.GetAsync(url).Result;
                  }
                  sw.Stop();
                  _executionTimes.Add((int)sw.ElapsedMilliseconds);
                  sw.Reset();
                }
                catch (Exception ex) {
                  if (_exceptionQueue.Count > 10) _exceptionQueue.Dequeue();
                  _exceptionQueue.Enqueue(ex);
                  sw.Stop();
                  sw.Reset();
                }
              }
            }
          }

          Thread.Sleep(1000);
        }
      }
      catch (Exception e) {

      }
    }

    private static string EscapeString(string Value) {
      if (String.IsNullOrEmpty(Value)) return "";
      return Value.Replace(@"\", @"\\").Replace("\"", "\\\"");
    }
  }


  internal class QueueItem {
    public string Method { get; set; }
    public string Details { get; set; }
  }
}
