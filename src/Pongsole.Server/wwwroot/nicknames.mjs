export const nicknames = Object.freeze([
  "MaybeToast", "PlotTwist", "FogGoblin", "EchoToast", "DoubtDonut",
  "HazyBagel", "OddMuffin", "FableFig", "BlurBerry", "MythMelon",
  "RumorRoll", "GhostGrape", "FauxPeach", "HushPlum", "VagueBean",
  "DreamDill", "GlitchKiwi", "JinxLemon", "WispApple", "MirageJam",

  "WasItSo", "DidItMove", "SawWhat", "YouSure", "WasThat",
  "WasItMe", "CouldBe", "MaybeNot", "ItDepends", "OrWasIt",
  "WasItEver", "AskAgain", "ThinkTwice", "WhoKnows", "GuessWho",
  "JustMaybe", "AlmostSo", "KindaTrue", "NotQuite", "SoItSeems",

  "WrongDoor", "OddMirror", "FogWindow", "FakeFloor", "TrapDoor",
  "SideDoor", "BackStairs", "SpareKey", "LoopRoom", "HazyHall",
  "VanishMat", "TiltTile", "SecretBox", "LostLatch", "GhostKnob",
  "BlindSpot", "FogLamp", "EchoHall", "WispRoom", "OddDrawer",

  "MoonAlias", "StarDecoy", "CloudFable", "SkyReplay", "SunMimic",
  "CometLie", "OrbitOops", "MoonHiccup", "StarRerun", "DuskDecoy",
  "DawnDouble", "Daydream", "SkyMuddle", "MoonPatch", "FogOrbit",
  "WispComet", "DreamStar", "FauxNova", "MetaMoon", "DuskGlitch",

  "DoubtDuck", "GhostGoose", "MythMoth", "EchoOwl", "FableFrog",
  "FogFerret", "OddOtter", "HazyHare", "WispWolf", "DecoyDeer",
  "FauxFinch", "MuddleMojo", "MaybeMole", "BlurBat", "FogFawn",
  "JinxJay", "PuzzledPug", "RiddleRam", "ShyShadow", "GhostCrab",

  "FakeFlash", "BlurFrame", "RerunRay", "LoopLens", "OddAngle",
  "TiltFocus", "EchoFilm", "SceneSkip", "DreamCut", "FauxFocus",
  "ZoomOops", "TapeTwist", "ShiftShot", "HazyFrame", "DoubtFilm",
  "FogFilter", "VanishCam", "WispFrame", "SoftFocus", "ReelMyth",

  "FizzFable", "MistMints", "MaybeMiso", "OddNoodle", "MetaMochi",
  "WispWaffle", "FauxFudge", "DoubtDip", "HazyHoney", "GhostGum",
  "BlinkCake", "MysteryPie", "PretzelPop", "MuddleMint", "TrickTaco",
  "FogTart", "DreamChai", "DecoyChips", "RumorRye", "ReplayRice",

  "TimeOops", "ClockFog", "LoopTick", "OnceAgain", "TimeFold",
  "LateBlink", "NowOrThen", "TimeTwist", "LostHour", "SkipBeat",
  "EchoClock", "DreamTick", "WasItNow", "WispWatch", "TickTale",
  "Yesterday", "RerunNoon", "AfterMaybe", "BeforeFog", "AlmostNow",

  "MindMaze", "RiddleBox", "DreamLogic", "EchoRiddle", "VagueClue",
  "FuzzyFact", "FauxProof", "MistTrace", "OddTheory", "ProofPoof",
  "ClueLoop", "GuessLoop", "PonderPop", "WonderFog", "ThinkFog",
  "CaseClosed", "CaseReopen", "NoteToSelf", "MythMemo", "RumorNote",

  "OddPong", "PaddlePop", "BallDecoy", "RallyLoop", "ServeFog",
  "PongAgain", "FauxBounce", "WispRally", "MirageBat", "BounceOops",
  "TiltRally", "HazyServe", "GlitchNet", "PhantomBat", "DreamDink",
  "EchoRally", "DoubtShot", "MatchMyth", "ScoreWhat", "PongFable",
]);

export function pickNickname(random = Math.random) {
  return nicknames[Math.floor(random() * nicknames.length)];
}
