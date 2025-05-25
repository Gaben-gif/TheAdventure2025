namespace TheAdventure.Models;

public class SpikeTrap : RenderableGameObject
{
    public SpikeTrap(SpriteSheet spriteSheet, (int X, int Y) position) 
        : base(spriteSheet, position)
    {
        // If your SpriteSheet class or the Load method doesn't automatically
        // activate a default animation, you might do it here:
        // this.SpriteSheet.ActivateAnimation("Idle");
        // However, the Engine.cs example activates it after loading.
    }
}