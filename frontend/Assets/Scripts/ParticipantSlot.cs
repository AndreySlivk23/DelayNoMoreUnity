using UnityEngine;
using shared;
using System;
using UnityEngine.UI;

public class ParticipantSlot : MonoBehaviour {
    public Image underlyingImg;

    // Start is called before the first frame update
    void Start() {

    }

    // Update is called once per frame
    void Update() {

    }

    public void SetAvatar(CharacterDownsync currCharacter) {
        if (null == currCharacter || Battle.TERMINATING_PLAYER_ID == currCharacter.Id) {
            underlyingImg.sprite = null;
            underlyingImg.gameObject.SetActive(false);
            return;
        }
        string speciesName = shared.Battle.characters[currCharacter.SpeciesId].SpeciesName;
        // Reference https://www.codeandweb.com/texturepacker/tutorials/using-spritesheets-with-unity#how-can-i-access-a-sprite-on-a-sprite-sheet-from-code
        string spriteSheetPath = String.Format("Characters/{0}/{0}", speciesName, speciesName);
        var sprites = Resources.LoadAll<Sprite>(spriteSheetPath);
        foreach (Sprite sprite in sprites) {
            if ("Avatar_1".Equals(sprite.name)) {
                underlyingImg.sprite = sprite;
                underlyingImg.gameObject.SetActive(true);
                break;
            }
        }
    }
}
