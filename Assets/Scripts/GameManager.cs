using System;
using System.Threading;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using KinematicCharacterController.Examples;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }
    
    public StagesData StarfallStages;

    public SessionData SessionData;

    public StagesData.Stage CurrentStage;

    //public static Leaderboards leaderboard { get; private set; }

    [Header("Player Character")] public APlayer aPlayer;
    public RangedWeapon playerWeapon { get; private set; }
    public Light dirLight;

    // TODO(mish): have SCharacter main player and set up a function to switch
    // between different players
    private SCharacter _currentPlayer;
    //private Kuze _kuze;
    
    // TODO(cameron): use more specific types if desired
    public static Action<APlayer> PlayerDeath;
    public static Action<GameObject> EnemyDeath;

    public static int finalScore;

    [FormerlySerializedAs("distortOnAwake")] [Header("Scene Loading")] [SerializeField]
    private bool distortOnStart = true;
    [SerializeField] private Material effectMaterial;
    [SerializeField] private float secondsToDistortOnSceneLoad = 5f;
    [SerializeField] private float distortionFromVertexResolution = 300f;
    [SerializeField] private float distortionFromVertexJitter = 0f;
    [SerializeField] private float distortionFromColorBalance = 0f;
    [SerializeField] private float distortionToVertexResolution = 5f;
    [SerializeField] private float distortionToVertexJitter = 3f;
    [SerializeField] private float distortionToColorBalance = 1f;

    [Header("Music")] 
    [SerializeField] private AudioSource _audioSource;

    private List<AudioClip> _playedSongs = new();

    [Header("Pooling")]
    private BloodPool _bloodPoolComponent;
    public ObjectPool<GameObject> BloodPool;

    private MuzzleFlashPool _muzzleFlashPoolComponent;
    public ObjectPool<GameObject> MuzzleFlashPool;

    private BulletTrailPool _bulletTrailPoolComponent;
    public ObjectPool<GameObject> BulletTrailPool;

    private CloneBulletTrailPool _cloneBulletTrailPoolComponent;
    public ObjectPool<GameObject> CloneBulletTrailPool;

    private RedLazerPool _redLazerPoolComponent;
    public ObjectPool<GameObject> RedLazerPool;

    private SurfaceImpactPool[] _surfaceImpactPoolComponents;
    public readonly Dictionary<ImpactEffectSurface.ImpactSurfaceType, ObjectPool<GameObject>> SurfaceImpactPools = new();

    private LinkPool _linkPoolComponent;
    public ObjectPool<GameObject> LinkPool;

    private bool _isAwakeDistorting;
    private bool _isLoadingNewScene;
    private float _distortionTimer = 0f;
    private string _sceneNameToLoad;
    private Dictionary<Renderer, List<Material>> _origRenderersMats = new();
    
    private static readonly int Color1 = Shader.PropertyToID("_Color");
    private static readonly int VertexResolution = Shader.PropertyToID("Vector1_B2CC132F");
    private static readonly int VertexDisplacmentAmount = Shader.PropertyToID("_VertexDisplacementAmount");
    private static readonly int DistortionColorBalance = Shader.PropertyToID("_DistortionColorBalance");
    
    [Button("InitLoadNewStage", "Load new stage", false)] public bool foo;
    [Button("WipeSessionData", "Wipe session data", false)] public bool bar;


    private void Awake()
    {
        //Initialize the instance of the Game Manager.
        if (Instance != null && Instance != this)
        {
            Destroy(this);
        }
        else
        {
            Instance = this;
        }
        
        //Leaderboards leaderboards = gameObject.AddComponent<Leaderboards>();

        if (!aPlayer && SceneManager.GetActiveScene().name != "MainMenu" && SceneManager.GetActiveScene().name != "Leaderboard")
        {
            TryFindAPlayer();
        }
        if (!dirLight) dirLight = TryFindDirLight();
        
        PlayerDeath += OnPlayerDeath;
        EnemyDeath += OnEnemyDeath;

        _bloodPoolComponent = GetComponent<BloodPool>();
        BloodPool = _bloodPoolComponent.Pool;

        _muzzleFlashPoolComponent = GetComponent<MuzzleFlashPool>();
        MuzzleFlashPool = _muzzleFlashPoolComponent.Pool;

        _bulletTrailPoolComponent = GetComponent<BulletTrailPool>();
        BulletTrailPool = _bulletTrailPoolComponent.Pool;

        _cloneBulletTrailPoolComponent = GetComponent<CloneBulletTrailPool>();
        CloneBulletTrailPool = _cloneBulletTrailPoolComponent.Pool;

        _redLazerPoolComponent = GetComponent<RedLazerPool>();
        RedLazerPool = _redLazerPoolComponent.Pool;

        _surfaceImpactPoolComponents = GetComponents<SurfaceImpactPool>();
        foreach (var component in _surfaceImpactPoolComponents)
        {
            SurfaceImpactPools.Add(component.surfaceType, component.Pool);
        }

        _linkPoolComponent = GetComponent<LinkPool>();
        LinkPool = _linkPoolComponent.Pool;
        
        //If we are in the main menu, wipe the sessionData.
        if (SceneManager.GetActiveScene().name == "MainMenu")
        {
            WipeSessionData();
        }

        foreach (var stage in StarfallStages.Stages)
        {
            if (stage.StageName == SceneManager.GetActiveScene().name)
            {
                CurrentStage = stage;
                break;
            }
        }

        // Set the scene as traversed, if it hasn't been marked as such already.
        if (SessionData.traversedStageNames.Count == 0)
        {
            SessionData.traversedStageNames.Add(SceneManager.GetActiveScene().name);
        }
        else
        {
            if (!SessionData.traversedStageNames.Contains(CurrentStage.StageName)) SessionData.traversedStageNames.Add(CurrentStage.StageName);
        }

        _playedSongs = new();
        if (!_audioSource) _audioSource = GetComponent<AudioSource>();
        if (_audioSource)
        {
            _audioSource.loop = false;
        }
    }

    private void Start()
    {
        if (!distortOnStart) return;
        SwapAllMaterialsInScene(true);
        _isAwakeDistorting = true;
    }

    public void WipeSessionData()
    {
        SessionData.sessionTotalTime = 0f;
        SessionData.sessionScore = 0;
        SessionData.traversedStageNames = new List<string>();
    }

    private void Update()
    {
        // Always add time to the session's total time.
        if (SceneManager.GetActiveScene().name != "MainMenu") SessionData.sessionTotalTime += Time.deltaTime;
        
        //Songs
        
        if (_audioSource && !_audioSource.isPlaying) PlayNewSong();
        
        //Distortion

        if (_isAwakeDistorting || _isLoadingNewScene) _distortionTimer += Time.deltaTime;
        
        if (_isAwakeDistorting && _distortionTimer >= secondsToDistortOnSceneLoad) StopAwakeDistorting();

        if (_isAwakeDistorting)
        {
            DoFullscreenDistortion(false);
        }
        
        if (_isLoadingNewScene)
        {
            DoFullscreenDistortion(true);
        }
        
    }

    void TryFindAPlayer()
    {
        aPlayer = GameObject.FindWithTag("Player").GetComponent<APlayer>();
    }

    Light TryFindDirLight()
    {
        foreach (var light in FindObjectsOfType<Light>())
        {
            if (light.type == LightType.Directional) return light;
        }

        return null;
    }
    
    private void OnPlayerDeath(APlayer player)
    {
        Debug.Log("SNAKE? SNAAAAAAAAAAAAAAAAKE!!!!");
        // Do whatever cleanup
        PlayerDeath -= OnPlayerDeath;
        EnemyDeath -= OnEnemyDeath;
        finalScore = (int)Score.getSavedScore();
        Debug.Log("final score " + finalScore);
        Invoke(nameof(LoadLeaderboardScene), 3.0f);
    }
    
    private void OnEnemyDeath(GameObject enemy)
    {
        Debug.Log("bitchass mofo dead");
        Destroy(enemy);
    }

    private void LoadLeaderboardScene()
    {
        SceneManager.LoadScene("Leaderboard", LoadSceneMode.Single);
    }

    public void InitLoadNewStage()
    {
        // For all stages in the game, which have we not visited?
        var unvisitedStages = (from stage in StarfallStages.Stages where !SessionData.traversedStageNames.Contains(stage.StageName) select stage.StageName).ToList();

        // If there are stages we haven't seen yet, choose a random one of those to load.
        if (unvisitedStages.Count > 0)
        {
            BeginLoadNewStage(unvisitedStages[Random.Range(0, unvisitedStages.Count)]);
            return;
        }
        
        // If we have visited every stage, we should continue to repeat the same pattern.
        // Get the index at which our current scene rests in traversedStages
        int thisIdx = 0;
        for (var i = 0; i < SessionData.traversedStageNames.Count; i++)
        {
            if (SessionData.traversedStageNames[i] == SceneManager.GetActiveScene().name)
            {
                thisIdx = i;
                break;
            }
        }

        // Load a new scene according to circular array logic
        BeginLoadNewStage(thisIdx + 1 >= SessionData.traversedStageNames.Count
            ? SessionData.traversedStageNames[0]
            : SessionData.traversedStageNames[thisIdx + 1]);
    }

    private void BeginLoadNewStage(string sceneName)
    {
        _sceneNameToLoad = sceneName;
        _distortionTimer = 0f;
        _isLoadingNewScene = true;
        SwapAllMaterialsInScene(true);
        StartCoroutine(WaitThenLoadScene(secondsToDistortOnSceneLoad));
    }

    private void SwapAllMaterialsInScene(bool useEffectMaterial)
    {
        
        // Every time this is called, we need all of these. 
        List<Renderer> allRenderers = new List<Renderer>();
        foreach (var t in FindObjectsOfType<Transform>())
        {
            foreach (var r in t.GetComponents<Renderer>())
            {
                allRenderers.Add(r);
            }
        }

        // If we are moving TO a distorted state, cache the current state, then assign the effect material.
        if (useEffectMaterial)
        {
            _origRenderersMats = new Dictionary<Renderer, List<Material>>();
            foreach (var r in allRenderers)
            {
                _origRenderersMats.Add(r, r.materials.ToList());
                var newMats = new Material[r.materials.Length];
                for (var i = 0; i < newMats.Length; i++)
                {
                    newMats[i] = effectMaterial;
                }

                r.materials = newMats;
            }
        }
        else // Use cached values to put back normal mats.
        {
            foreach (var kv in _origRenderersMats)
            {
                var newMats = new Material[kv.Key.materials.Length];
                for (var i = 0; i < newMats.Length; i++)
                {
                    newMats[i] = kv.Value[i];
                }

                kv.Key.materials = newMats;
            }
        }
        
    }

    private void DoFullscreenDistortion(bool to)
    {
        foreach (var t in FindObjectsOfType<Transform>())
        {
            foreach (var r in t.GetComponents<Renderer>())
            {
                foreach (var m in r.materials)
                {
                    m.SetFloat(VertexResolution, to ? Mathf.Lerp(distortionFromVertexResolution, distortionToVertexResolution, _distortionTimer / secondsToDistortOnSceneLoad) : Mathf.Lerp(distortionToVertexResolution, distortionFromVertexResolution, _distortionTimer / secondsToDistortOnSceneLoad));
                    m.SetFloat(VertexDisplacmentAmount, to ? Mathf.Lerp(distortionFromVertexJitter, distortionToVertexJitter, _distortionTimer / secondsToDistortOnSceneLoad) : Mathf.Lerp(distortionToVertexJitter, distortionFromVertexJitter, _distortionTimer / secondsToDistortOnSceneLoad));
                    m.SetFloat(DistortionColorBalance, to ? Mathf.Lerp(distortionFromColorBalance, distortionToColorBalance, _distortionTimer / secondsToDistortOnSceneLoad) : Mathf.Lerp(distortionToColorBalance, distortionFromColorBalance, _distortionTimer / secondsToDistortOnSceneLoad));
                }
            }
        }
    }

    void StopAwakeDistorting()
    {
        _isAwakeDistorting = false;
        _distortionTimer = 0f;
        SwapAllMaterialsInScene(false);
    }

    IEnumerator WaitThenLoadScene(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        SceneManager.LoadScene(_sceneNameToLoad);
    }

    void PlayNewSong()
    {
        if (SceneManager.GetActiveScene().name == "MainMenu") return;
        if (SceneManager.GetActiveScene().name == "Leaderboard") return;
        Debug.Log(CurrentStage.StageName);
        if (!_audioSource) return;
        List<AudioClip> unplayedSongs = new();

        foreach (var audioClip in CurrentStage.StageSongs)
        {
            if (!_playedSongs.Contains(audioClip)) unplayedSongs.Add(audioClip);
        }

        var songToPlay = unplayedSongs.Count == 0 ? CurrentStage.StageSongs[Random.Range(0, CurrentStage.StageSongs.Length - 1)] : unplayedSongs[Random.Range(0, unplayedSongs.Count)];
        _audioSource.clip = songToPlay;
        _audioSource.Play();
        _playedSongs.Add(songToPlay);
    }

    
}
