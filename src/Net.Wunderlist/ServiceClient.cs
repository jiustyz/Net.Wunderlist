﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Wunderlist
{
	public sealed class ServiceClient : IDisposable
	{
		private readonly Lazy<IStorageProvider> storage;
		private readonly HttpClient client;
		private bool disposed = false;

		private static readonly JsonSerializerSettings jsonSettings;

		#region Constructors

		public ServiceClient(string accessToken, string clientSecret)
			: this(accessToken, clientSecret, () => Internal.ServiceAdapter.DefaultStorageProvider())
		{
		}

		public ServiceClient(string accessToken, string clientSecret, Func<IStorageProvider> storageFactory)
		{
			this.client = new HttpClient(Authorization.GetAuthorizationHandler(null, accessToken, clientSecret));
			this.client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(Constants.MediaTypeNames.Json));
			this.storage = new Lazy<IStorageProvider>(storageFactory);

			this.Files = new FileContext(this);
			this.Lists = new ListContext(this);
			this.Memberships = new MembershipContext(this);
			this.Reminders = new ReminderContext(this);
			this.Comments = new CommentContext(this);
			this.Tasks = new TaskContext(this);
			this.Subtasks = new SubtaskContext(this);
			this.Users = new UserContext(this);
			this.Webhooks = new WebhookContext(this);
		}

		static ServiceClient()
		{
			ServiceClient.jsonSettings = new JsonSerializerSettings
			{
				Error = new EventHandler<ErrorEventArgs>((s, e) => OnSerializationError(e))
			};
			ServiceClient.jsonSettings.Converters.Add(new Internal.ResourceCreationConverter());
		}

		#endregion

		public IFileInfo Files { get; private set; }

		public IListInfo Lists { get; private set; }

		public IMembershipInfo Memberships { get; private set; }

		public IReminderInfo Reminders { get; private set; }

		public ICommentInfo Comments { get; private set; }

		public ITaskInfo Tasks { get; private set; }

		public ISubtaskInfo Subtasks { get; private set; }

		public IUserInfo Users { get; private set; }

		public IWebhookInfo Webhooks { get; private set; }

		#region Disposal Implementation

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		private void Dispose(bool disposing)
		{
			if (!this.disposed)
			{
				if (disposing) // dispose aggregated resources
					this.client.Dispose();
				this.disposed = true; // disposing has been done
			}
		}

		#endregion

		#region Request Handling Methods

		private Task<T> GetAsync<T>(string cmd, IDictionary<string, object> parameters, CancellationToken cancellationToken)
		{
			return GetAsync(cmd, parameters, cancellationToken).ContinueWith(t => Deserialize<T>(t.Result)).Unwrap();
		}

		private Task<HttpResponseMessage> GetAsync(string cmd, IDictionary<string, object> parameters, CancellationToken cancellationToken)
		{
			TaskCompletionSource<HttpResponseMessage> tcs = new TaskCompletionSource<HttpResponseMessage>();

			this.client.GetAsync(CreateRequestUri(cmd, parameters),
				HttpCompletionOption.ResponseHeadersRead, cancellationToken)
				.ContinueWith(t => HandleResponseCompletion(t, tcs));
			return tcs.Task;
		}

		private Task<T> PatchAsync<T>(string cmd, IDictionary<string, object> parameters, IDictionary<string, object> content, CancellationToken cancellationToken)
		{
			return PatchAsync(cmd, parameters, content, cancellationToken).ContinueWith(t => Deserialize<T>(t.Result)).Unwrap();
		}

		private Task<HttpResponseMessage> PatchAsync(string cmd, IDictionary<string, object> parameters, IDictionary<string, object> content, CancellationToken cancellationToken)
		{
			TaskCompletionSource<HttpResponseMessage> tcs = new TaskCompletionSource<HttpResponseMessage>();
			var request = new HttpRequestMessage(new HttpMethod("PATCH"), CreateRequestUri(cmd, parameters)) { Content = CreateRequestContent(content) };

			this.client.SendAsync(request, cancellationToken)
				.ContinueWith(t => HandleResponseCompletion(t, tcs));
			return tcs.Task;
		}

		private Task<T> SendAsync<T>(string cmd, IDictionary<string, object> parameters, IDictionary<string, object> content, HttpMethod method, CancellationToken cancellationToken)
		{
			return SendAsync(cmd, parameters, content, method, cancellationToken).ContinueWith(t => Deserialize<T>(t.Result)).Unwrap();
		}

		private Task<HttpResponseMessage> SendAsync(string cmd, IDictionary<string, object> parameters, IDictionary<string, object> content, HttpMethod method, CancellationToken cancellationToken)
		{
			TaskCompletionSource<HttpResponseMessage> tcs = new TaskCompletionSource<HttpResponseMessage>();
			var request = new HttpRequestMessage(method, CreateRequestUri(cmd, parameters)) { Content = CreateRequestContent(content) };

			this.client.SendAsync(request, cancellationToken)
				.ContinueWith(t => HandleResponseCompletion(t, tcs));
			return tcs.Task;
		}

		private static async Task<T> Deserialize<T>(HttpResponseMessage response)
		{
			string responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#if DEBUG
			Diagnostics.Debug.WriteLine(responseJson);
#endif
			return JsonConvert.DeserializeObject<T>(responseJson, jsonSettings);
		}

		private static async Task<JObject> DeserializeDynamic(HttpResponseMessage response)
		{
			string responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#if DEBUG
			Diagnostics.Debug.WriteLine(responseJson);
#endif
			return (JObject)JsonConvert.DeserializeObject(responseJson);
		}

		private static void HandleResponseCompletion(Task<HttpResponseMessage> task, TaskCompletionSource<HttpResponseMessage> tcs)
		{
			if (task.IsCanceled) tcs.TrySetCanceled();
			else if (!task.Result.IsSuccessStatusCode && task.Result.StatusCode != HttpStatusCode.Found)
			{
				if (task.Result.Content != null && 
					task.Result.Content.Headers.ContentType.MediaType.Equals(Constants.MediaTypeNames.Json))
				{
					task.Result.Content.ReadAsStringAsync().ContinueWith(t2 =>
					{
						JObject status = (JObject)JsonConvert.DeserializeObject(t2.Result);
						tcs.TrySetException(new ServiceException((int)task.Result.StatusCode, (JObject)status["error"]));
					});
				}
				else tcs.TrySetException(new ServiceException((int)task.Result.StatusCode, task.Result.ReasonPhrase));
			}
			else if (task.IsFaulted) tcs.TrySetException(task.Exception);
			else if (task.IsCompleted) tcs.TrySetResult(task.Result);
		}


		private Uri CreateRequestUri(string cmd, IDictionary<string, object> parameters)
		{
			var builder = new UriBuilder(String.Concat("https://a.wunderlist.com/api/v1/", cmd));

			if (parameters != null)
			{
				string query = String.Join("&", parameters.Where(s => s.Value != null)
					.Select(s => String.Concat(s.Key, "=", ConvertParameterValue(s.Value, true))));
#if DEBUG
				Diagnostics.Debug.WriteLine(query);
#endif
				builder.Query = query;
			}
			return builder.Uri;
		}

		private HttpContent CreateRequestContent(IDictionary<string, object> content)
		{
			if (content != null)
			{
				var contentObject = (new JObject(content.Where(s => s.Value != null)
					.Select(s =>
					{
						if (!(s.Value is string) && s.Value is Collections.IEnumerable)
							return new JProperty(s.Key, new JArray(s.Value));

						return new JProperty(s.Key, s.Value);
					})
					.ToArray()));
				return new StringContent(contentObject.ToString(), null, Constants.MediaTypeNames.Json);
			}
			return null;
		}

		private static string ConvertParameterValue(object value, bool escapeStrings)
		{
			Type t = value.GetType();
			t = Nullable.GetUnderlyingType(t) ?? t;

#if NETFX_CORE || PORTABLE
			if (t == typeof(Enum)) return Enum.GetName(value.GetType(), value).ToLower();
#else
			if (t.IsEnum) return Enum.GetName(value.GetType(), value).ToLower();
#endif
			else if (t == typeof(DateTime)) return ((DateTime)value).ToString(CultureInfo.InvariantCulture);
			else if (t == typeof(int)) return ((int)value).ToString(CultureInfo.InvariantCulture);
			else if (t == typeof(bool)) return ((bool)value) ? "1" : "0";

			return escapeStrings ? Uri.EscapeDataString(value.ToString()) : value.ToString();
		}

		internal static string BuildCommand(string resourceType, int? id)
		{
			return id.HasValue ? String.Join("/", resourceType, id) : resourceType;
		}

		#endregion

		#region Serialization Event Handlers

		private static void OnSerializationError(Newtonsoft.Json.Serialization.ErrorEventArgs args)
		{
			Diagnostics.Debug.WriteLine(args.ErrorContext.Error.Message);
			args.ErrorContext.Handled = true;
		}

		#endregion

		#region Nested Classes

		private sealed class FileContext : IFileInfo
		{
			private ServiceClient client;

			internal FileContext(ServiceClient client)
			{
				this.client = client;
			}

			public async Task<File> CreateAsync(int taskId, string filename, CancellationToken cancellationToken)
			{
				var storageProvider = client.storage.Value;
				string contentType = (await storageProvider.GetMimeTypeAsync(filename)) ?? Constants.MediaTypeNames.Octet;

				using (var content = await storageProvider.OpenAsync(filename, cancellationToken))
				{
					var uploadResource = (JObject)await UploadStartAsync(IO.Path.GetFileName(filename),
						(int)content.Length, contentType, null, null, cancellationToken);

					int uploadId = uploadResource.Value<int>("id");
					int requiredParts = (int)(content.Length / 5242880) + ((content.Length % 5242880 > 0) ? 1 : 0);
					var createdAt = DateTime.UtcNow;

					if (requiredParts > 1 && content.CanSeek)
					{
						await UploadFilePartAsync(new Internal.ChunkRestrictedStream(0, 5242880, () => content),
							uploadResource.Value<JObject>("part"), cancellationToken);

						for (int part = 2; part <= requiredParts && !cancellationToken.IsCancellationRequested; part++)
						{
							var nextUploadResource = await UploadNextAsync(uploadId, part, null, cancellationToken);
							await UploadFilePartAsync(new Internal.ChunkRestrictedStream((part - 1) * 5242880, 5242880, () => content),
								nextUploadResource.Value<JObject>("part"), cancellationToken);
						}
					}
					else await UploadFilePartAsync(content, uploadResource.Value<JObject>("part"), cancellationToken);

					await UploadFinishedAsync(uploadId, cancellationToken);
					//await Task.Delay(TimeSpan.FromMinutes((requiredParts > 1) ? 1 : 0), cancellationToken);

					return await CreateInternalAsync(taskId, uploadId, createdAt, cancellationToken);
				}
			}

			private async Task<File> CreateInternalAsync(int taskId, int uploadId, DateTime? createdAt, CancellationToken cancellationToken)
			{
				var requestContent = new Dictionary<string, object> { { "upload_id", uploadId }, { "task_id", taskId }, { "local_created_at", createdAt } };
				return await client.SendAsync<File>("files", null, requestContent, HttpMethod.Post, cancellationToken).ConfigureAwait(false);
			}

			private async Task UploadFilePartAsync(IO.Stream stream, JObject data, CancellationToken cancellationToken)
			{
				using (var uploadClient = new HttpClient(new HttpClientHandler { UseCookies = false }))
				{
					var request = new HttpRequestMessage(HttpMethod.Put, data.Value<string>("url")) { Content = new StreamContent(stream) };
					request.Headers.Add("Authorization", data.Value<string>("authorization"));
					request.Headers.Add("x-amz-date", data.Value<string>("date"));

					var response = await uploadClient.SendAsync(request, cancellationToken);
					response.EnsureSuccessStatusCode();
				}
			}

			private async Task<JObject> UploadStartAsync(string name, int contentLength, string contentType, int? part, string md5sum, CancellationToken cancellationToken)
			{
				var requestContent = new Dictionary<string, object> { { "file_name", name }, { "file_size", contentLength }, { "content_type", contentType }, { "part_number", part }, { "md5sum", md5sum } };
				var response = await client.SendAsync("uploads", null, requestContent, HttpMethod.Post, cancellationToken).ConfigureAwait(false);
				return await DeserializeDynamic(response);
			}

			private async Task<JObject> UploadNextAsync(int uploadId, int part, string md5sum, CancellationToken cancellationToken)
			{
				string cmd = String.Format("uploads/{0}/parts", uploadId);
				var parameters = new Dictionary<string, object> { { "part_number", part }, { "md5sum", md5sum } };
				var response = await client.GetAsync(cmd, parameters, cancellationToken).ConfigureAwait(false);
				return await DeserializeDynamic(response);
			}

			private async Task<JObject> UploadFinishedAsync(int uploadId, CancellationToken cancellationToken)
			{
				var requestContent = new Dictionary<string, object> { { "state", "finished" } };
				var response = await client.PatchAsync(ServiceClient.BuildCommand("uploads", uploadId), null, requestContent, cancellationToken).ConfigureAwait(false);
				return await DeserializeDynamic(response);
			}

			public async Task DeleteAsync(int id, int revision, CancellationToken cancellationToken)
			{
				var parameters = new Dictionary<string, object> { { "revision", revision } };
				await client.SendAsync(ServiceClient.BuildCommand("files", id), parameters, null, HttpMethod.Delete, cancellationToken).ConfigureAwait(false);
			}

			public async Task<File> GetAsync(int id, CancellationToken cancellationToken)
			{
				return await client.GetAsync<File>(ServiceClient.BuildCommand("files", id), null, cancellationToken).ConfigureAwait(false);
			}

			public async Task<IEnumerable<File>> GetByListAsync(int listId, CancellationToken cancellationToken)
			{
				var parameters = new Dictionary<string, object> { { "list_id", listId } };
				return await client.GetAsync<IEnumerable<File>>("files", parameters, cancellationToken).ConfigureAwait(false);
			}

			public async Task<IEnumerable<File>> GetByTaskAsync(int taskId, CancellationToken cancellationToken)
			{
				var parameters = new Dictionary<string, object> { { "task_id", taskId } };
				return await client.GetAsync<IEnumerable<File>>("files", parameters, cancellationToken).ConfigureAwait(false);
			}

			public async Task<Uri> GetPreviewAsync(int id, PreviewPlatform? platform, bool retina, CancellationToken cancellationToken)
			{
				var parameters = new Dictionary<string, object> { { "file_id", id }, { "platform", platform }, { "size", retina ? "retina" : "nonretina" } };
				var response = await client.GetAsync("previews", parameters, cancellationToken).ConfigureAwait(false);
				return new Uri((await DeserializeDynamic(response)).Value<string>("url"));
			}
		}

		private sealed class ListContext : IListInfo
		{
			private ServiceClient client;

			internal ListContext(ServiceClient client)
			{
				this.client = client;
			}

			public async Task<IEnumerable<List>> GetAsync(CancellationToken cancellationToken)
			{
				return await client.GetAsync<IEnumerable<List>>("lists", null, cancellationToken).ConfigureAwait(false);
			}

			public async Task<List> GetAsync(int id, CancellationToken cancellationToken)
			{
				return await client.GetAsync<List>(ServiceClient.BuildCommand("lists", id), null, cancellationToken).ConfigureAwait(false);
			}

			public async Task<Positions> GetPositionAsync(int id, CancellationToken cancellationToken)
			{
				return await client.GetAsync<Positions>(ServiceClient.BuildCommand("list_positions", id), null, cancellationToken).ConfigureAwait(false);
			}

			public async Task<IEnumerable<Positions>> GetPositionsAsync(CancellationToken cancellationToken)
			{
				return await client.GetAsync<IEnumerable<Positions>>("list_positions", null, cancellationToken).ConfigureAwait(false);
			}

			public async Task<bool> ChangeStateAsync(int id, int revision, bool makePublic, CancellationToken cancellationToken)
			{
				var requestContent = new Dictionary<string, object> { { "revision", revision }, { "public", makePublic } };
				var response = await client.PatchAsync(ServiceClient.BuildCommand("lists", id), null, requestContent, cancellationToken).ConfigureAwait(false);
				return (await DeserializeDynamic(response)).Value<bool>("public");

			}

			public async Task<List> CreateAsync(string name, CancellationToken cancellationToken)
			{
				var requestContent = new Dictionary<string, object> { { "title", name } };
				return await client.SendAsync<List>("lists", null, requestContent, HttpMethod.Post, cancellationToken).ConfigureAwait(false);
			}

			public async Task<List> UpdateAsync(int id, int revision, string name, CancellationToken cancellationToken)
			{
				var requestContent = new Dictionary<string, object> { { "revision", revision }, { "title", name } };
				return await client.PatchAsync<List>(ServiceClient.BuildCommand("lists", id), null, requestContent, cancellationToken).ConfigureAwait(false);
			}

			public async Task<Positions> UpdatePositionAsync(int id, int revision, IEnumerable<int> positions, CancellationToken cancellationToken)
			{
				var requestContent = new Dictionary<string, object> { { "revision", revision }, { "values", positions } };
				return await client.PatchAsync<Positions>(ServiceClient.BuildCommand("list_positions", id), null, requestContent, cancellationToken).ConfigureAwait(false);
			}

			public async Task DeleteAsync(int id, int revision, CancellationToken cancellationToken)
			{
				var parameters = new Dictionary<string, object> { { "revision", revision } };
				await client.SendAsync(ServiceClient.BuildCommand("lists", id), parameters, null, HttpMethod.Delete, cancellationToken).ConfigureAwait(false);
			}
		}

		private sealed class MembershipContext : IMembershipInfo
		{
			private ServiceClient client;

			internal MembershipContext(ServiceClient client)
			{
				this.client = client;
			}

			public async Task<Membership> UpdateAsync(int id, int revision, MembershipState state, bool muted, CancellationToken cancellationToken)
			{
				var requestContent = new Dictionary<string, object> { { "revision", revision }, { "state", state }, { "muted", muted } };
				return await client.PatchAsync<Membership>(ServiceClient.BuildCommand("memberships", id), null, requestContent, cancellationToken).ConfigureAwait(false);
			}

			public async Task<Membership> CreateAsync(int listId, string email, bool muted, CancellationToken cancellationToken)
			{
				var requestContent = new Dictionary<string, object> { { "list_id", listId }, { "email", email }, { "muted", muted } };
				return await client.SendAsync<Membership>("memberships", null, requestContent, HttpMethod.Post, cancellationToken).ConfigureAwait(false);
			}

			public async Task<Membership> CreateAsync(int listId, int userId, bool muted, CancellationToken cancellationToken)
			{
				var requestContent = new Dictionary<string, object> { { "list_id", listId }, { "user_id", userId }, { "muted", muted } };
				return await client.SendAsync<Membership>("memberships", null, requestContent, HttpMethod.Post, cancellationToken).ConfigureAwait(false);
			}

			public async Task DeleteAsync(int id, int revision, CancellationToken cancellationToken)
			{
				var parameters = new Dictionary<string, object> { { "revision", revision } };
				await client.SendAsync(ServiceClient.BuildCommand("memberships", id), parameters, null, HttpMethod.Delete, cancellationToken).ConfigureAwait(false);
			}

			public async Task<IEnumerable<Membership>> GetAsync(int? listId, CancellationToken cancellationToken)
			{
				return await client.GetAsync<IEnumerable<Membership>>(ServiceClient.BuildCommand("memberships", listId), null, cancellationToken).ConfigureAwait(false);
			}
		}

		private sealed class ReminderContext : IReminderInfo
		{
			private ServiceClient client;

			internal ReminderContext(ServiceClient client)
			{
				this.client = client;
			}

			public async Task<Reminder> CreateAsync(int taskId, DateTime date, string deviceUdid, CancellationToken cancellationToken)
			{
				var requestContent = new Dictionary<string, object> { { "task_id", taskId }, { "date", date }, { "created_by_device_udid", deviceUdid } };
				return await client.SendAsync<Reminder>("reminders", null, requestContent, HttpMethod.Post, cancellationToken).ConfigureAwait(false);
			}

			public async Task DeleteAsync(int id, int revision, CancellationToken cancellationToken)
			{
				var parameters = new Dictionary<string, object> { { "revision", revision } };
				await client.SendAsync(ServiceClient.BuildCommand("reminders", id), parameters, null, HttpMethod.Delete, cancellationToken).ConfigureAwait(false);
			}

			public async Task<Reminder> GetAsync(int id, CancellationToken cancellationToken)
			{
				return await client.GetAsync<Reminder>(ServiceClient.BuildCommand("reminders", id), null, cancellationToken).ConfigureAwait(false);
			}

			public async Task<IEnumerable<Reminder>> GetByListAsync(int listId, CancellationToken cancellationToken)
			{
				var parameters = new Dictionary<string, object> { { "list_id", listId } };
				return await client.GetAsync<IEnumerable<Reminder>>("reminders", parameters, cancellationToken).ConfigureAwait(false);
			}

			public async Task<IEnumerable<Reminder>> GetByTaskAsync(int taskId, CancellationToken cancellationToken)
			{
				var parameters = new Dictionary<string, object> { { "task_id", taskId } };
				return await client.GetAsync<IEnumerable<Reminder>>("reminders", parameters, cancellationToken).ConfigureAwait(false);
			}

			public async Task<Reminder> UpdateAsync(int id, int revision, DateTime date, CancellationToken cancellationToken)
			{
				var requestContent = new Dictionary<string, object> { { "revision", revision }, { "date", date } };
				return await client.PatchAsync<Reminder>(ServiceClient.BuildCommand("reminders", id), null, requestContent, cancellationToken).ConfigureAwait(false);
			}
		}

		private sealed class CommentContext : ICommentInfo
		{
			private ServiceClient client;

			internal CommentContext(ServiceClient client)
			{
				this.client = client;
			}

			public async Task<Comment> CreateAsync(int taskId, string comment, CancellationToken cancellationToken)
			{
				var requestContent = new Dictionary<string, object> { { "task_id", taskId }, { "text", comment } };
				return await client.SendAsync<Comment>("task_comments", null, requestContent, HttpMethod.Post, cancellationToken).ConfigureAwait(false);
			}

			public async Task DeleteAsync(int id, int revision, CancellationToken cancellationToken)
			{
				var parameters = new Dictionary<string, object> { { "revision", revision } };
				await client.SendAsync(ServiceClient.BuildCommand("task_comments", id), parameters, null, HttpMethod.Delete, cancellationToken).ConfigureAwait(false);
			}

			public async Task<Comment> GetAsync(int id, CancellationToken cancellationToken)
			{
				return await client.GetAsync<Comment>(ServiceClient.BuildCommand("task_comments", id), null, cancellationToken).ConfigureAwait(false);
			}

			public async Task<IEnumerable<Comment>> GetByListAsync(int listId, CancellationToken cancellationToken)
			{
				var parameters = new Dictionary<string, object> { { "list_id", listId } };
				return await client.GetAsync<IEnumerable<Comment>>("task_comments", parameters, cancellationToken).ConfigureAwait(false);
			}

			public async Task<IEnumerable<Comment>> GetByTaskAsync(int taskId, CancellationToken cancellationToken)
			{
				var parameters = new Dictionary<string, object> { { "task_id", taskId } };
				return await client.GetAsync<IEnumerable<Comment>>("task_comments", parameters, cancellationToken).ConfigureAwait(false);
			}

			public async Task<Comment> UpdateAsync(int id, int revision, string comment, CancellationToken cancellationToken)
			{
				var requestContent = new Dictionary<string, object> { { "revision", revision }, { "text", comment } };
				return await client.PatchAsync<Comment>(ServiceClient.BuildCommand("task_comments", id), null, requestContent, cancellationToken).ConfigureAwait(false);
			}
		}

		private sealed class TaskContext : ITaskInfo
		{
			private ServiceClient client;

			internal TaskContext(ServiceClient client)
			{
				this.client = client;
			}

			public async Task<MainTask> GetAsync(int id, CancellationToken cancellationToken)
			{
				return await client.GetAsync<MainTask>(ServiceClient.BuildCommand("tasks", id), null, cancellationToken).ConfigureAwait(false);
			}

			public async Task<IEnumerable<MainTask>> GetAsync(int listId, bool? completed, CancellationToken cancellationToken)
			{
				var parameters = new Dictionary<string, object> { { "list_id", listId }, { "completed", completed } };
				return await client.GetAsync<IEnumerable<MainTask>>("tasks", parameters, cancellationToken).ConfigureAwait(false);
			}

			public async Task<Positions> GetPositionAsync(int id, CancellationToken cancellationToken)
			{
				return await client.GetAsync<Positions>(ServiceClient.BuildCommand("task_positions", id), null, cancellationToken).ConfigureAwait(false);
			}

			public async Task<IEnumerable<Positions>> GetPositionsAsync(int listId, CancellationToken cancellationToken)
			{
				var parameters = new Dictionary<string, object> { { "list_id", listId } };
				return await client.GetAsync<IEnumerable<Positions>>("task_positions", parameters, cancellationToken).ConfigureAwait(false);
			}

			public async Task<MainTask> CreateAsync(int listId, string name, int? assigneeId, bool? completed, RecurrenceType? recurrenceType, int? recurrenceCount, DateTime? dueDate, bool? starred, CancellationToken cancellationToken)
			{
				var requestContent = new Dictionary<string, object> { { "list_id", listId }, { "title", name }, { "assignee_id", assigneeId }, { "completed", completed }, { "recurrence_type", recurrenceType }, { "due_date", dueDate }, { "starred", starred } };

				if (recurrenceType.HasValue)
				{
					if (!recurrenceCount.HasValue) throw new ArgumentNullException("recurrenceCount");
					requestContent.Add("recurrence_count", recurrenceCount);
				}
				return await client.SendAsync<MainTask>("tasks", null, requestContent, HttpMethod.Post, cancellationToken).ConfigureAwait(false);
			}

			public async Task<MainTask> UpdateAsync(int id, int revision, string name, int? assigneeId, bool? completed, RecurrenceType? recurrenceType, int? recurrenceCount, DateTime? dueDate, bool? starred, CancellationToken cancellationToken)
			{
				var requestContent = new Dictionary<string, object> { { "revision", revision }, { "title", name }, { "assignee_id", assigneeId }, { "completed", completed }, { "recurrence_type", recurrenceType }, { "due_date", dueDate }, { "starred", starred } };
				requestContent.Add("remove", requestContent.Where(s => s.Value == null).ToArray());

				if (recurrenceType.HasValue)
				{
					if (!recurrenceCount.HasValue) throw new ArgumentNullException("recurrenceCount");
					requestContent.Add("recurrence_count", recurrenceCount);
				}
				return await client.PatchAsync<MainTask>(ServiceClient.BuildCommand("tasks", id), null, requestContent, cancellationToken).ConfigureAwait(false);
			}

			public async Task<Positions> UpdatePositionAsync(int id, int revision, IEnumerable<int> positions, CancellationToken cancellationToken)
			{
				var requestContent = new Dictionary<string, object> { { "revision", revision }, { "values", positions } };
				return await client.PatchAsync<Positions>(ServiceClient.BuildCommand("task_positions", id), null, requestContent, cancellationToken).ConfigureAwait(false);
			}

			public async Task DeleteAsync(int id, int revision, CancellationToken cancellationToken)
			{
				var parameters = new Dictionary<string, object> { { "revision", revision } };
				await client.SendAsync(ServiceClient.BuildCommand("tasks", id), parameters, null, HttpMethod.Delete, cancellationToken).ConfigureAwait(false);
			}
		}

		private sealed class SubtaskContext : ISubtaskInfo
		{
			private ServiceClient client;

			internal SubtaskContext(ServiceClient client)
			{
				this.client = client;
			}

			public async Task<SubTask> GetAsync(int id, CancellationToken cancellationToken)
			{
				return await client.GetAsync<SubTask>(ServiceClient.BuildCommand("subtasks", id), null, cancellationToken).ConfigureAwait(false);
			}

			public async Task<IEnumerable<SubTask>> GetByListAsync(int listId, bool? completed, CancellationToken cancellationToken)
			{
				var parameters = new Dictionary<string, object> { { "list_id", listId }, { "completed", completed } };
				return await client.GetAsync<IEnumerable<SubTask>>("subtasks", parameters, cancellationToken).ConfigureAwait(false);
			}

			public async Task<IEnumerable<SubTask>> GetByTaskAsync(int taskId, bool? completed, CancellationToken cancellationToken)
			{
				var parameters = new Dictionary<string, object> { { "task_id", taskId }, { "completed", completed } };
				return await client.GetAsync<IEnumerable<SubTask>>("subtasks", parameters, cancellationToken).ConfigureAwait(false);
			}

			public async Task<Positions> GetPositionAsync(int id, CancellationToken cancellationToken)
			{
				return await client.GetAsync<Positions>(ServiceClient.BuildCommand("subtask_positions", id), null, cancellationToken).ConfigureAwait(false);
			}

			public async Task<IEnumerable<Positions>> GetPositionsByListAsync(int listId, CancellationToken cancellationToken)
			{
				var parameters = new Dictionary<string, object> { { "list_id", listId } };
				return await client.GetAsync<IEnumerable<Positions>>("subtask_positions", parameters, cancellationToken).ConfigureAwait(false);
			}

			public async Task<IEnumerable<Positions>> GetPositionsByTaskAsync(int taskId, CancellationToken cancellationToken)
			{
				var parameters = new Dictionary<string, object> { { "task_id", taskId } };
				return await client.GetAsync<IEnumerable<Positions>>("subtask_positions", parameters, cancellationToken).ConfigureAwait(false);
			}

			public async Task<SubTask> CreateAsync(int taskId, string name, bool? completed, CancellationToken cancellationToken)
			{
				var requestContent = new Dictionary<string, object> { { "task_id", taskId }, { "title", name }, { "completed", completed } };
				return await client.SendAsync<SubTask>("subtasks", null, requestContent, HttpMethod.Post, cancellationToken).ConfigureAwait(false);
			}

			public async Task<SubTask> UpdateAsync(int id, int revision, string name, bool? completed, CancellationToken cancellationToken)
			{
				var requestContent = new Dictionary<string, object> { { "revision", revision }, { "title", name }, { "completed", completed } };
				return await client.PatchAsync<SubTask>(ServiceClient.BuildCommand("subtasks", id), null, requestContent, cancellationToken).ConfigureAwait(false);
			}

			public async Task<Positions> UpdatePositionAsync(int id, int revision, IEnumerable<int> positions, CancellationToken cancellationToken)
			{
				var requestContent = new Dictionary<string, object> { { "revision", revision }, { "values", positions } };
				return await client.PatchAsync<Positions>(ServiceClient.BuildCommand("subtask_positions", id), null, requestContent, cancellationToken).ConfigureAwait(false);
			}

			public async Task DeleteAsync(int id, int revision, CancellationToken cancellationToken)
			{
				var parameters = new Dictionary<string, object> { { "revision", revision } };
				await client.SendAsync(ServiceClient.BuildCommand("subtasks", id), parameters, null, HttpMethod.Delete, cancellationToken).ConfigureAwait(false);
			}
		}

		private sealed class UserContext : IUserInfo
		{
			private ServiceClient client;

			internal UserContext(ServiceClient client)
			{
				this.client = client;
			}

			public async Task<User> GetAsync(CancellationToken cancellationToken)
			{
				return await client.GetAsync<User>("user", null, cancellationToken).ConfigureAwait(false);
			}

			public async Task<IEnumerable<User>> GetAsync(int? listId, CancellationToken cancellationToken)
			{
				var parameters = new Dictionary<string, object> { { "list_id", listId } };
				return await client.GetAsync<IEnumerable<User>>("users", parameters, cancellationToken).ConfigureAwait(false);

			}

			public async Task<int> GetRootAsync(CancellationToken cancellationToken)
			{
				var response = await client.GetAsync("root", null, cancellationToken).ConfigureAwait(false);
				return (await DeserializeDynamic(response)).Value<int>("id");
			}

			public async Task<Uri> GetAvatarAsync(int userId, int? size, bool fallback, CancellationToken cancellationToken)
			{
				var parameters = new Dictionary<string, object>
				{
					{ "user_id", userId }, { "size", size }, { "fallback", fallback }
				};
				var response = await client.GetAsync("avatar", parameters, cancellationToken).ConfigureAwait(false);
				return response.Headers.Location;
			}
		}

		private sealed class WebhookContext : IWebhookInfo
		{
			private ServiceClient client;

			internal WebhookContext(ServiceClient client)
			{
				this.client = client;
			}

			public async Task<Webhook> CreateAsync(int listId, Uri endpoint, string processorType, string configuration, CancellationToken cancellationToken)
			{
				var requestContent = new Dictionary<string, object> { { "list_id", listId }, { "url", endpoint }, { "processor_type", processorType }, { "configuration", configuration } };
				return await client.SendAsync<Webhook>("webhooks", null, requestContent, HttpMethod.Post, cancellationToken).ConfigureAwait(false);
			}

			public async Task DeleteAsync(int id, int revision, CancellationToken cancellationToken)
			{
				var parameters = new Dictionary<string, object> { { "revision", revision } };
				await client.SendAsync(ServiceClient.BuildCommand("webhooks", id), parameters, null, HttpMethod.Delete, cancellationToken).ConfigureAwait(false);
			}

			public async Task<IEnumerable<Webhook>> GetByListAsync(int listId, CancellationToken cancellationToken)
			{
				var parameters = new Dictionary<string, object> { { "list_id", listId } };
				return await client.GetAsync<IEnumerable<Webhook>>("webhooks", parameters, cancellationToken).ConfigureAwait(false);
			}
		}

		#endregion
	}
}
