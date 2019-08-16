using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Common.Audio;
using Common.GameTask;
using Common.Service;
using Extensions;
using UniRx;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Networking;
using UnityEngine.U2D;
using Zenject;
using AudioSettings = Common.Audio.AudioSettings;

namespace Common.BundleManager
{
	// Вспомогательные классы для сериализации манифеста бандлов
	[Serializable]
	public class BundleDescription
	{
		[SerializeField] private string _name;
		[SerializeField] private string _hash;

		public string Name => _name;
		public string Hash => _hash;
		public Hash128 Hash128 => Hash128.Parse(_hash);

		public BundleDescription(string name, string hash)
		{
			_name = name;
			_hash = hash;
		}
	}

	[Serializable]
	public class BundleManifest
	{
		[SerializeField] private List<BundleDescription> _bundles;

		public BundleDescription[] Bundles => _bundles.ToArray();

		public BundleManifest(List<BundleDescription> bundles)
		{
			_bundles = bundles;
		}
	}
	//---------------------------------------//


	/// <summary>
	/// Сервис управления загрузчиками бандлов.
	/// </summary>
	public class BundleManager : IGameService
	{
		/// <summary>
		/// Класс загрузчика бандла, он же служит для освобождения ресурсов через IDisposable.
		/// </summary>
		public class BundleLoader : IGameTask, IDisposable
		{
			private readonly BundleDescription _description;
			private readonly IAudioManager _audioManager;
			private readonly BoolReactiveProperty _complete = new BoolReactiveProperty(false);

			private readonly Dictionary<string, SpriteAtlas> _loadedAtlases = new Dictionary<string, SpriteAtlas>();
			private readonly HashSet<string> _clips = new HashSet<string>();

			private bool _isDisposed;
			private Coroutine _loadRoutine;

			public BundleLoader(BundleDescription description, IAudioManager audioManager)
			{
				_description = description;
				_audioManager = audioManager;
			}

			// IDisposable

			public void Dispose()
			{
				if (_isDisposed) return;
				_isDisposed = true;

				if (_loadedAtlases.Any())
				{
					SpriteAtlasManager.atlasRequested -= OnAtlasRequest;
					_loadedAtlases.Clear();
				}
				
				_audioManager.UnregisterClips(_clips);
				_clips.Clear();

				BundleLoadersMap.Remove(BundleName);

				if (Complete.Value)
				{
					Bundle.Unload(true);
					Bundle = null;
				}

				_complete.Dispose();
			}

			// \IDisposable

			// IGameTask

			public void Start()
			{
				if (Complete.Value || _loadRoutine != null || _isDisposed) return;
				_loadRoutine = MainThreadDispatcher.StartCoroutine(GetAssetBundle(BundleUrl, _description.Hash128));
			}

			public IReadOnlyReactiveProperty<bool> Complete => _complete;

			// \IGameTask

			private void ManageAtlases()
			{
				Assert.IsFalse(_loadedAtlases.Any());

				Bundle.LoadAllAssets<SpriteAtlas>().ToList()
					.ForEach(atlas => _loadedAtlases.Add(atlas.name, atlas));

				if (_loadedAtlases.Any())
				{
					SpriteAtlasManager.atlasRequested += OnAtlasRequest;
				}
			}

			private void ManageSounds()
			{
				Assert.IsFalse(_clips.Any());

				Bundle.LoadAllAssets<AudioSettings>().ToList()
					.ForEach(settings =>
					{
						var clips = settings.Clips;
						foreach (var pair in clips)
						{
							_audioManager.RegisterClips(pair.Value, pair.Key);
							pair.Value.Keys.ToList().ForEach(s => _clips.Add(s));
						}
					});
			}

			private void OnAtlasRequest(string atlasName, Action<SpriteAtlas> callback)
			{
				callback?.Invoke(_loadedAtlases.TryGetValue(atlasName, out var atlas) ? atlas : null);
			}

			private IEnumerator GetAssetBundle(string url, Hash128 hash)
			{
				DebugConditional.LogFormat("-- load bundle from {0}...", url);
				var www = UnityWebRequestAssetBundle.GetAssetBundle(url, hash);
				yield return www.SendWebRequest();

				if (www.isNetworkError || www.isHttpError)
				{
					throw new Exception($"Failed to load bundle {url} with error: {www.error}");
				}

				_loadRoutine = null;

				Bundle = DownloadHandlerAssetBundle.GetContent(www);

				if (_isDisposed)
				{
					Bundle.Unload(true);
					Bundle = null;
				}
				else
				{
					ManageAtlases();
					ManageSounds();
					DebugConditional.Log("... bundle loaded successfully.");
					_complete.SetValueAndForceNotify(true);
				}
			}

			/// <summary>
			/// Имя бандла.
			/// </summary>
			public string BundleName => _description.Name;

			/// <summary>
			/// URL бандла.
			/// </summary>
			public string BundleUrl
			{
				get
				{
					var path = $@"{Application.streamingAssetsPath}/Bundles/{BundleName}";
						
#if UNITY_IOS
					path = $"file://{path}";
#endif
					return path;
				}
			}

			/// <summary>
			/// Загруженный бандл.
			/// </summary>
			public AssetBundle Bundle { get; private set; }
		}
		//---------------------------------------//


		private readonly BoolReactiveProperty _ready = new BoolReactiveProperty(false);
		private Dictionary<string, BundleDescription> _bundleDataMap;

		private static readonly Dictionary<string, BundleLoader> BundleLoadersMap =
			new Dictionary<string, BundleLoader>();

#pragma warning disable 649
		[Inject] private readonly IAudioManager _audioManager;
#pragma warning restore 649

		// IGameService

		public void Initialize()
		{
			var manifestPath = $@"{Application.streamingAssetsPath}/Bundles/manifest.json";
						
#if UNITY_IOS
			manifestPath = $"file://{manifestPath}";
#endif
			MainThreadDispatcher.StartCoroutine(LoadManifest(manifestPath));
		}

		public IReadOnlyReactiveProperty<bool> Ready => _ready;

		// \IGameService

		/// <summary>
		/// Получить загрузчик бандла.
		/// </summary>
		/// <param name="bundleName">Имя бандла, для которого получается загрузчик.</param>
		/// <returns>Загрузчик бандла.</returns>
		public BundleLoader GetBundleLoader(string bundleName)
		{
			if (!Ready.Value)
			{
				Debug.LogError("BundleManager is not initialized yet.");
				return null;
			}

			if (BundleLoadersMap.TryGetValue(bundleName, out var loader)) return loader;

			if (!_bundleDataMap.TryGetValue(bundleName, out var data))
			{
				Debug.LogErrorFormat("There is no data for bundle {0} in manifest.", bundleName);
				return null;
			}

			loader = new BundleLoader(data,_audioManager);
			BundleLoadersMap.Add(bundleName, loader);
			return loader;
		}

		private IEnumerator LoadManifest(string path)
		{
			DebugConditional.LogFormat("-- load bundles manifest from {0}...", path);
			using (var www = UnityWebRequest.Get(path))
			{
				yield return www.SendWebRequest();

				if (www.isNetworkError || www.isHttpError)
				{
					Debug.LogErrorFormat("Failed to load manifest from {0} with error: {1}", path, www.error);
				}
				else
				{
					var manifest = JsonUtility.FromJson<BundleManifest>(www.downloadHandler.text);
					_bundleDataMap = manifest.Bundles.ToDictionary(description => description.Name);
					DebugConditional.Log("... manifest loaded successfully.");
					_ready.SetValueAndForceNotify(true);
				}
			}
		}
	}
}