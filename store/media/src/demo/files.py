PLAYER_V1 = """using UnityEngine;

namespace Skyward.Player
{
    [RequireComponent(typeof(CharacterController))]
    public class PlayerController : MonoBehaviour
    {
        [SerializeField] private float moveSpeed = 5f;

        private CharacterController _controller;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
        }

        private void Update()
        {
            var input = new Vector3(Input.GetAxis("Horizontal"), 0f, Input.GetAxis("Vertical"));
            _controller.Move(input * moveSpeed * Time.deltaTime);
        }
    }
}
"""

PLAYER_V2 = """using UnityEngine;

namespace Skyward.Player
{
    [RequireComponent(typeof(CharacterController))]
    public class PlayerController : MonoBehaviour
    {
        [SerializeField] private float moveSpeed = 5f;
        [SerializeField] private float sprintMultiplier = 1.6f;
        [SerializeField] private float jumpHeight = 1.2f;
        [SerializeField] private float gravity = -9.81f;

        private CharacterController _controller;
        private Vector3 _velocity;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
        }

        private void Update()
        {
            var input = new Vector3(Input.GetAxis("Horizontal"), 0f, Input.GetAxis("Vertical"));
            var speed = Input.GetKey(KeyCode.LeftShift) ? moveSpeed * sprintMultiplier : moveSpeed;
            _controller.Move(input * speed * Time.deltaTime);

            if (_controller.isGrounded && Input.GetButtonDown("Jump"))
            {
                _velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
            }

            _velocity.y += gravity * Time.deltaTime;
            _controller.Move(_velocity * Time.deltaTime);
        }
    }
}
"""

PLAYER_V3 = """using UnityEngine;

namespace Skyward.Player
{
    [RequireComponent(typeof(CharacterController))]
    public class PlayerController : MonoBehaviour
    {
        [SerializeField] private float moveSpeed = 5.5f;
        [SerializeField] private float sprintMultiplier = 1.6f;
        [SerializeField] private float jumpHeight = 1.4f;
        [SerializeField] private float gravity = -18f;

        private CharacterController _controller;
        private Vector3 _velocity;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
        }

        private void Update()
        {
            var input = new Vector3(Input.GetAxis("Horizontal"), 0f, Input.GetAxis("Vertical"));
            var speed = Input.GetKey(KeyCode.LeftShift) ? moveSpeed * sprintMultiplier : moveSpeed;
            _controller.Move(Vector3.ClampMagnitude(input, 1f) * speed * Time.deltaTime);

            if (_controller.isGrounded && _velocity.y < 0f)
            {
                _velocity.y = -2f;
            }

            if (_controller.isGrounded && Input.GetButtonDown("Jump"))
            {
                _velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
            }

            _velocity.y += gravity * Time.deltaTime;
            _controller.Move(_velocity * Time.deltaTime);
        }
    }
}
"""

PLAYER_V4 = """using UnityEngine;

namespace Skyward.Player
{
    [RequireComponent(typeof(CharacterController))]
    public class PlayerController : MonoBehaviour
    {
        [SerializeField] private float moveSpeed = 5.5f;
        [SerializeField] private float sprintMultiplier = 1.6f;
        [SerializeField] private float jumpHeight = 1.4f;
        [SerializeField] private float gravity = -18f;
        [SerializeField] private float dashDistance = 4f;
        [SerializeField] private float dashCooldown = 0.8f;

        private CharacterController _controller;
        private Vector3 _velocity;
        private float _nextDash;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
        }

        private void Update()
        {
            var input = new Vector3(Input.GetAxis("Horizontal"), 0f, Input.GetAxis("Vertical"));
            var speed = Input.GetKey(KeyCode.LeftShift) ? moveSpeed * sprintMultiplier : moveSpeed;
            _controller.Move(Vector3.ClampMagnitude(input, 1f) * speed * Time.deltaTime);

            if (Input.GetKeyDown(KeyCode.Q) && Time.time >= _nextDash && input.sqrMagnitude > 0.01f)
            {
                _controller.Move(input.normalized * dashDistance);
                _nextDash = Time.time + dashCooldown;
            }

            if (_controller.isGrounded && _velocity.y < 0f)
            {
                _velocity.y = -2f;
            }

            if (_controller.isGrounded && Input.GetButtonDown("Jump"))
            {
                _velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
            }

            _velocity.y += gravity * Time.deltaTime;
            _controller.Move(_velocity * Time.deltaTime);
        }
    }
}
"""

PLAYER_HEALTH = """using System;
using UnityEngine;

namespace Skyward.Player
{
    public class PlayerHealth : MonoBehaviour
    {
        [SerializeField] private int maxHealth = 100;

        public int Current { get; private set; }
        public int Max => maxHealth;

        public event Action<int> Changed;
        public event Action Died;

        private void Awake()
        {
            Current = maxHealth;
        }

        public void Damage(int amount)
        {
            if (Current <= 0)
            {
                return;
            }

            Current = Mathf.Max(0, Current - amount);
            Changed?.Invoke(Current);
            if (Current == 0)
            {
                Died?.Invoke();
            }
        }

        public void Heal(int amount)
        {
            Current = Mathf.Min(maxHealth, Current + amount);
            Changed?.Invoke(Current);
        }
    }
}
"""

CAMERA_V1 = """using UnityEngine;

namespace Skyward.Gameplay
{
    public class CameraFollow : MonoBehaviour
    {
        [SerializeField] private Transform target;
        [SerializeField] private Vector3 offset = new Vector3(0f, 8f, -6f);
        [SerializeField] private float smoothing = 6f;

        private void Update()
        {
            if (target == null)
            {
                return;
            }

            var desired = target.position + offset;
            transform.position = Vector3.Lerp(transform.position, desired, smoothing * Time.deltaTime);
            transform.LookAt(target);
        }
    }
}
"""

CAMERA_V2 = """using UnityEngine;

namespace Skyward.Gameplay
{
    public class CameraFollow : MonoBehaviour
    {
        [SerializeField] private Transform target;
        [SerializeField] private Vector3 offset = new Vector3(0f, 8f, -6f);
        [SerializeField] private float smoothTime = 0.15f;

        private Vector3 _velocity;

        private void LateUpdate()
        {
            if (target == null)
            {
                return;
            }

            var desired = target.position + offset;
            transform.position = Vector3.SmoothDamp(transform.position, desired, ref _velocity, smoothTime);
            transform.LookAt(target);
        }
    }
}
"""

ENEMY_V1 = """using Skyward.Player;
using UnityEngine;
using UnityEngine.AI;

namespace Skyward.Enemies
{
    [RequireComponent(typeof(NavMeshAgent))]
    public class EnemyAI : MonoBehaviour
    {
        [SerializeField] private float sightRange = 12f;
        [SerializeField] private float attackRange = 1.8f;
        [SerializeField] private int damage = 10;
        [SerializeField] private float attackCooldown = 1.2f;

        private NavMeshAgent _agent;
        private PlayerHealth _player;
        private float _nextAttack;

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            _player = FindAnyObjectByType<PlayerHealth>();
        }

        private void Update()
        {
            if (_player == null)
            {
                return;
            }

            var distance = Vector3.Distance(transform.position, _player.transform.position);
            if (distance > sightRange)
            {
                _agent.ResetPath();
                return;
            }

            _agent.SetDestination(_player.transform.position);
            if (distance <= attackRange && Time.time >= _nextAttack)
            {
                _player.Damage(damage);
                _nextAttack = Time.time + attackCooldown;
            }
        }
    }
}
"""

ENEMY_V2 = ENEMY_V1.replace(
    "        [SerializeField] private float attackCooldown = 1.2f;\n",
    "        [SerializeField] private float attackCooldown = 1.2f;\n        [SerializeField] private float loseInterestAfter = 3f;\n",
).replace(
    "        private float _nextAttack;\n",
    "        private float _nextAttack;\n        private float _lastSeen;\n",
).replace(
    """            if (distance > sightRange)
            {
                _agent.ResetPath();
                return;
            }
""",
    """            if (distance <= sightRange)
            {
                _lastSeen = Time.time;
            }
            else if (Time.time - _lastSeen > loseInterestAfter)
            {
                _agent.ResetPath();
                return;
            }
""",
)

SPAWNER = """using UnityEngine;

namespace Skyward.Enemies
{
    public class Spawner : MonoBehaviour
    {
        [SerializeField] private EnemyAI prefab;
        [SerializeField] private Transform[] points;
        [SerializeField] private float interval = 4f;
        [SerializeField] private int maxAlive = 6;

        private float _nextSpawn;

        private void Update()
        {
            if (Time.time < _nextSpawn || points.Length == 0)
            {
                return;
            }

            _nextSpawn = Time.time + interval;
            if (transform.childCount >= maxAlive)
            {
                return;
            }

            var point = points[Random.Range(0, points.Length)];
            Instantiate(prefab, point.position, point.rotation, transform);
        }
    }
}
"""

BOSS = """using UnityEngine;

namespace Skyward.Enemies
{
    public class BossFight : MonoBehaviour
    {
        [SerializeField] private EnemyAI boss;
        [SerializeField] private Spawner[] reinforcements;
        [SerializeField] private float enrageBelow = 0.3f;

        private bool _enraged;

        public void OnBossDamaged(float healthFraction)
        {
            if (_enraged || healthFraction > enrageBelow)
            {
                return;
            }

            _enraged = true;
            foreach (var spawner in reinforcements)
            {
                spawner.enabled = true;
            }
        }
    }
}
"""

ITEM = """using UnityEngine;

namespace Skyward.Inventory
{
    [CreateAssetMenu(menuName = "Skyward/Item")]
    public class Item : ScriptableObject
    {
        public string displayName;
        public Sprite icon;
        public int maxStack = 1;
        public int healAmount;
    }
}
"""

INVENTORY = """using System;
using System.Collections.Generic;
using UnityEngine;

namespace Skyward.Inventory
{
    public class Inventory : MonoBehaviour
    {
        [SerializeField] private int slots = 12;

        private readonly Dictionary<Item, int> _items = new Dictionary<Item, int>();

        public event Action Changed;

        public bool Add(Item item, int count = 1)
        {
            _items.TryGetValue(item, out var current);
            if (current == 0 && _items.Count >= slots)
            {
                return false;
            }

            _items[item] = Mathf.Min(item.maxStack, current + count);
            Changed?.Invoke();
            return true;
        }

        public bool Remove(Item item)
        {
            if (!_items.TryGetValue(item, out var current))
            {
                return false;
            }

            if (current <= 1)
            {
                _items.Remove(item);
            }
            else
            {
                _items[item] = current - 1;
            }

            Changed?.Invoke();
            return true;
        }

        public int Count(Item item) => _items.TryGetValue(item, out var count) ? count : 0;
    }
}
"""

HEALTH_BAR = """using Skyward.Player;
using UnityEngine;
using UnityEngine.UIElements;

namespace Skyward.UI
{
    public class HealthBar : MonoBehaviour
    {
        [SerializeField] private UIDocument document;
        [SerializeField] private PlayerHealth health;

        private ProgressBar _bar;

        private void OnEnable()
        {
            _bar = document.rootVisualElement.Q<ProgressBar>("health");
            health.Changed += Refresh;
            Refresh(health.Current);
        }

        private void OnDisable()
        {
            health.Changed -= Refresh;
        }

        private void Refresh(int current)
        {
            _bar.highValue = health.Max;
            _bar.value = current;
            _bar.title = $"{current} / {health.Max}";
        }
    }
}
"""

DESIGN_NOTES = """# Boss fight

- Arena: north tower, two reinforcement spawners.
- Boss enrages below 30% health and calls reinforcements.
- Open question: should the dash go through enemies?
"""

GITIGNORE = """/[Ll]ibrary/
/[Tt]emp/
/[Oo]bj/
/[Bb]uild/
/[Bb]uilds/
/[Ll]ogs/
/[Uu]ser[Ss]ettings/
*.csproj
*.sln
*.user
.idea/
.vs/
"""

SETUP = """using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class DemoSetup
{
    public static void Build()
    {
        Directory.CreateDirectory("Assets/Game/Materials");
        Directory.CreateDirectory("Assets/Game/Prefabs");
        Directory.CreateDirectory("Assets/Game/Scenes");
        Directory.CreateDirectory("Assets/Game/Items");
        AssetDatabase.Refresh();

        var shader = Shader.Find("Standard");
        var ground = new Material(shader) { color = new Color(0.32f, 0.42f, 0.30f) };
        AssetDatabase.CreateAsset(ground, "Assets/Game/Materials/Ground.mat");
        var playerMaterial = new Material(shader) { color = new Color(0.20f, 0.55f, 0.85f) };
        AssetDatabase.CreateAsset(playerMaterial, "Assets/Game/Materials/Player.mat");
        var enemyMaterial = new Material(shader) { color = new Color(0.80f, 0.25f, 0.22f) };
        AssetDatabase.CreateAsset(enemyMaterial, "Assets/Game/Materials/Enemy.mat");

        var player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        player.name = "Player";
        player.GetComponent<Renderer>().sharedMaterial = playerMaterial;
        PrefabUtility.SaveAsPrefabAsset(player, "Assets/Game/Prefabs/Player.prefab");
        Object.DestroyImmediate(player);

        var enemy = GameObject.CreatePrimitive(PrimitiveType.Cube);
        enemy.name = "Enemy";
        enemy.GetComponent<Renderer>().sharedMaterial = enemyMaterial;
        PrefabUtility.SaveAsPrefabAsset(enemy, "Assets/Game/Prefabs/Enemy.prefab");
        Object.DestroyImmediate(enemy);

        foreach (var name in new[] { "Potion", "Key", "Coin" })
        {
            var item = ScriptableObject.CreateInstance("Skyward.Inventory.Item");
            AssetDatabase.CreateAsset(item, $"Assets/Game/Items/{name}.asset");
        }

        var main = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
        plane.name = "Ground";
        plane.transform.localScale = new Vector3(5f, 1f, 5f);
        plane.GetComponent<Renderer>().sharedMaterial = ground;
        PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Game/Prefabs/Player.prefab"));
        EditorSceneManager.SaveScene(main, "Assets/Game/Scenes/Main.unity");

        var menu = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        EditorSceneManager.SaveScene(menu, "Assets/Game/Scenes/Menu.unity");

        AssetDatabase.SaveAssets();
        EditorApplication.Exit(0);
    }
}
"""
