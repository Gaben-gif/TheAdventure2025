using System.Reflection;
using System.Text.Json;
using Silk.NET.Maths;
using TheAdventure.Models;
using TheAdventure.Models.Data;
using TheAdventure.Scripting;
using System; 
using System.Linq; 
using System.Collections.Generic;

namespace TheAdventure;

public class Engine
{
    private readonly GameRenderer _renderer;
    private readonly Input _input;
    private readonly ScriptEngine _scriptEngine = new();

    private readonly Dictionary<int, GameObject> _gameObjects = new();
    private readonly Dictionary<string, TileSet> _loadedTileSets = new();
    private readonly Dictionary<int, Tile> _tileIdMap = new();

    private Level _currentLevel = new();
    private PlayerObject? _player;

    private DateTimeOffset _lastUpdate = DateTimeOffset.Now;

    public Engine(GameRenderer renderer, Input input)
    {
        _renderer = renderer;
        _input = input;
        _input.OnMouseClick += (_, coords) => AddBomb(coords.x, coords.y);
    }

    public void SetupWorld()
    {
        _player = new(SpriteSheet.Load(_renderer, "Player.json", "Assets"), 100, 100);

        var levelContent = File.ReadAllText(Path.Combine("Assets", "terrain.tmj"));
        var level = JsonSerializer.Deserialize<Level>(levelContent);
        if (level == null) { throw new Exception("Failed to load level"); }

        foreach (var tileSetRef in level.TileSets)
        {
            var tileSetContent = File.ReadAllText(Path.Combine("Assets", tileSetRef.Source));
            var tileSet = JsonSerializer.Deserialize<TileSet>(tileSetContent);
            if (tileSet == null) { throw new Exception("Failed to load tile set"); }

            foreach (var tile in tileSet.Tiles)
            {
                tile.TextureId = _renderer.LoadTexture(Path.Combine("Assets", tile.Image), out _);
                _tileIdMap.Add(tile.Id!.Value, tile);
            }
            _loadedTileSets.Add(tileSet.Name, tileSet);
        }

        if (level.Width == null || level.Height == null) { throw new Exception("Invalid level dimensions"); }
        if (level.TileWidth == null || level.TileHeight == null) { throw new Exception("Invalid tile dimensions"); }
        
        _renderer.SetWorldBounds(new Rectangle<int>(0, 0, level.Width.Value * level.TileWidth.Value, level.Height.Value * level.TileHeight.Value));
        _currentLevel = level;
        _scriptEngine.LoadAll(Path.Combine("Assets", "Scripts"));
        
        try
        {
            SpriteSheet spikeSpriteSheet = SpriteSheet.Load(_renderer, "Traps/spikes_spritesheet.json", "Assets/Data");
            spikeSpriteSheet.ActivateAnimation("Idle");

            SpikeTrap spike1 = new SpikeTrap(spikeSpriteSheet, (200, 250)); 
            _gameObjects.Add(spike1.Id, spike1);

            SpikeTrap spike2 = new SpikeTrap(spikeSpriteSheet, (300, 250)); 
            _gameObjects.Add(spike2.Id, spike2);
            
            Console.WriteLine("Spike traps created.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load or create spike traps: {ex.ToString()}");
        }

        Console.WriteLine("Engine: World setup complete.");
    }

    private bool DoRectanglesOverlap(Rectangle<int> r1, Rectangle<int> r2)
    {
        if (r1.Origin.X + r1.Size.X <= r2.Origin.X || r2.Origin.X + r2.Size.X <= r1.Origin.X)
            return false;
        if (r1.Origin.Y + r1.Size.Y <= r2.Origin.Y || r2.Origin.Y + r2.Size.Y <= r1.Origin.Y)
            return false;
        return true;
    }

    public void ProcessFrame()
    {
        var currentTime = DateTimeOffset.Now;
        var msSinceLastFrame = (currentTime - _lastUpdate).TotalMilliseconds;
        _lastUpdate = currentTime;

        if (_player == null || _player.State.State == PlayerObject.PlayerState.GameOver)
        {
            return; 
        }
            
        double up = _input.IsUpPressed() ? 1.0 : 0.0;
        double down = _input.IsDownPressed() ? 1.0 : 0.0;
        double left = _input.IsLeftPressed() ? 1.0 : 0.0;
        double right = _input.IsRightPressed() ? 1.0 : 0.0;
        bool isAttacking = _input.IsKeyAPressed() && (up + down + left + right <= 1);
        bool addBombInput = _input.IsKeyBPressed();

        _player.UpdatePosition(up, down, left, right, 48, 48, msSinceLastFrame);
        if (isAttacking)
        {
            _player.Attack();
        }
        
        _scriptEngine.ExecuteAll(this);

        if (addBombInput)
        {
            if(_player != null) AddBomb(_player.Position.X, _player.Position.Y, false);
        }

        if (_player.State.State != PlayerObject.PlayerState.GameOver) 
        {
             Rectangle<int> playerBounds = new Rectangle<int>(
                _player.Position.X + _player.SpriteSheet.FrameCenter.OffsetX - (_player.SpriteSheet.FrameWidth / 4),
                _player.Position.Y + _player.SpriteSheet.FrameCenter.OffsetY - (_player.SpriteSheet.FrameHeight / 4),
                _player.SpriteSheet.FrameWidth / 2,
                _player.SpriteSheet.FrameHeight / 2);

            foreach (var gameObject in _gameObjects.Values)
            {
                if (gameObject is SpikeTrap spike)
                {
                    Rectangle<int> spikeBounds = new Rectangle<int>(
                        spike.Position.X + spike.SpriteSheet.FrameCenter.OffsetX - (spike.SpriteSheet.FrameWidth / 2), 
                        spike.Position.Y + spike.SpriteSheet.FrameCenter.OffsetY - (spike.SpriteSheet.FrameHeight / 2),
                        spike.SpriteSheet.FrameWidth, 
                        spike.SpriteSheet.FrameHeight);

                    if (DoRectanglesOverlap(playerBounds, spikeBounds))
                    {
                        Console.WriteLine("Player hit spikes! Game Over.");
                        _player.SetState(PlayerObject.PlayerState.GameOver, _player.State.Direction);
                        break; 
                    }
                }
            }
        }
    }

    public void RenderFrame()
    {
        _renderer.SetDrawColor(0, 0, 0, 255);
        _renderer.ClearScreen();

        if (_player != null) 
        {
            var playerPosition = _player.Position; 
            _renderer.CameraLookAt(playerPosition.X, playerPosition.Y);
        }

        RenderTerrain();
        RenderAllObjects();

        _renderer.PresentFrame();
    }

    public void RenderAllObjects() 
    {
        var toRemove = new List<int>();
        foreach (var gameObject in new List<GameObject>(_gameObjects.Values).OfType<RenderableGameObject>()) 
        {
            gameObject.Render(_renderer);
            if (gameObject is TemporaryGameObject { IsExpired: true } tempGameObject)
            {
                toRemove.Add(tempGameObject.Id);
            }
        }

        foreach (var id in toRemove)
        {
            _gameObjects.Remove(id, out var removedGameObject);
            if (_player == null || _player.State.State == PlayerObject.PlayerState.GameOver || removedGameObject == null) continue;

            if (removedGameObject is TemporaryGameObject tempGameObject) 
            {
                Rectangle<int> playerHitbox = new Rectangle<int>(
                    _player.Position.X + _player.SpriteSheet.FrameCenter.OffsetX - (_player.SpriteSheet.FrameWidth / 4),
                    _player.Position.Y + _player.SpriteSheet.FrameCenter.OffsetY - (_player.SpriteSheet.FrameHeight / 4),
                    _player.SpriteSheet.FrameWidth / 2,
                    _player.SpriteSheet.FrameHeight / 2);

                 Rectangle<int> bombHitbox = new Rectangle<int>(
                    tempGameObject.Position.X - tempGameObject.SpriteSheet.FrameWidth / 2, 
                    tempGameObject.Position.Y - tempGameObject.SpriteSheet.FrameHeight / 2,
                    tempGameObject.SpriteSheet.FrameWidth,
                    tempGameObject.SpriteSheet.FrameHeight);

                if (DoRectanglesOverlap(playerHitbox, bombHitbox))
                {
                    Console.WriteLine("Player hit by bomb explosion! Game Over.");
                    _player.SetState(PlayerObject.PlayerState.GameOver, _player.State.Direction);
                }
            }
        }
        _player?.Render(_renderer);
    }

    public void RenderTerrain() 
    {
        foreach (var currentLayer in _currentLevel.Layers)
        {
            if (currentLayer.Width == null || _currentLevel.Width == null || _currentLevel.Height == null || currentLayer.Data == null) continue;

            for (int i = 0; i < _currentLevel.Width.Value; ++i)
            {
                for (int j = 0; j < _currentLevel.Height.Value; ++j)
                {
                    int dataIndex = j * currentLayer.Width.Value + i;
                    if (dataIndex >= currentLayer.Data.Count) continue; 

                    var currentTileGid = currentLayer.Data[dataIndex];
                    if (currentTileGid == null || currentTileGid.Value == 0) { continue; }
                    
                    var currentTileId = currentTileGid.Value -1; 
                    if (!_tileIdMap.TryGetValue(currentTileId, out var currentTile)) { continue; }
                    
                    var tileWidth = currentTile.ImageWidth ?? 0;
                    var tileHeight = currentTile.ImageHeight ?? 0;
                    if (tileWidth == 0 || tileHeight == 0) continue;

                    var sourceRect = new Rectangle<int>(0, 0, tileWidth, tileHeight);
                    var destRect = new Rectangle<int>(i * tileWidth, j * tileHeight, tileWidth, tileHeight);
                    _renderer.RenderTexture(currentTile.TextureId, sourceRect, destRect);
                }
            }
        }
    }

    public IEnumerable<RenderableGameObject> GetRenderables() 
    {
        return new List<GameObject>(_gameObjects.Values).OfType<RenderableGameObject>();
    }

    public (int X, int Y) GetPlayerPosition() 
    {
        if (_player == null) return (0,0); 
        return _player.Position; 
    }

    public void AddBomb(int X, int Y, bool translateCoordinates = true) 
    {
        var worldCoords = translateCoordinates ? _renderer.ToWorldCoordinates(X, Y) : new Vector2D<int>(X, Y);
        SpriteSheet spriteSheet = SpriteSheet.Load(_renderer, "BombExploding.json", "Assets");
        spriteSheet.ActivateAnimation("Explode");
        TemporaryGameObject bomb = new(spriteSheet, 2.1, (worldCoords.X, worldCoords.Y));
        _gameObjects.Add(bomb.Id, bomb);
    }
}