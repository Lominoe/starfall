using System;
using System.Collections;
using System.Collections.Generic;
using TheKiwiCoder;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

public class SAi : SCharacter
{
    [Range(0, 100)]
    public float linkValue = 20;

    public int numLinkDrops = 1;

    protected BehaviourTreeRunner TreeRunner;
    protected BehaviourTree BehaviourTree;
    protected SAiInputs Inputs;
    
    public LookAtBehavior lookAtBehavior = LookAtBehavior.AtPath;
    
    public NavMeshPath NavMeshPath;
    public PathStatus pathStatus = PathStatus.None;
    public float stopDistance = .1f;

    public TriggerStatus triggerStatus = TriggerStatus.isUp;
    public AimStatus aimStatus = AimStatus.isUp;
    public SCharacter targetChar;

    public bool isTargetedByPriestess = false;

    //Event, used to update score when enemy dies
    public static event Action<SAi> OnAIDeath;
    
    //Event, used to trigger a hitmarker when hit
    public static event Action<SAi, float> OnAIHit;
    
    //Accuracy ranges from 0 to 1, and is referenced by various firing and ability cast methods.
    //An accuracy of 1 means every shot will hit, or will at least be fired at the perfect center/led to properly hit assuming 
    //the same trajectory of the target entity.
    [Range(0f, 1f)]
    public float accuracy = 1f;

    public Collider coll;
    
    protected int CurrCornersIndex = 0;

    [Header("Debug")] public bool debug = false;

    [SerializeField] private float killIfHasNotMovedInSeconds = 10f;
    private bool _isRunningKillIfHasNotMovedInSeconds;

    public enum LookAtBehavior
    {
        AtPath,
        AtTargetCharacter
    }

    public enum PathStatus
    {
        Pending,
        Reached,
        Failed,
        None
    }

    public enum TriggerStatus
    {
        isHeld,
        isUp
    }

    public enum AimStatus
    {
        isHeld,
        isUp
    }

    protected override void StartCharacter()
    {
        base.StartCharacter();
        NavMeshPath = new NavMeshPath();
        coll = GetComponent<Collider>();
        TreeRunner = GetComponent<BehaviourTreeRunner>();
        if (!TreeRunner)
        {
            Debug.LogWarning($"Entity {gameObject} has no attached behaviorTreeRunner.");
        }
    }
    
    protected override void HandleInputs()
    {
        InitInputs();
        if (lookAtBehavior == LookAtBehavior.AtTargetCharacter) LookAtTargetCharacter();
        ExecuteAim();
        if (triggerStatus == TriggerStatus.isHeld && _weapon && _weapon.enabled) ExecuteFire();
        MoveAgent();
    }

    void InitInputs()
    {
        Inputs = new SAiInputs();
    }

    protected override void UpdateCharacter()
    {
        AssignInputs();
        if (!_isRunningKillIfHasNotMovedInSeconds) StartCoroutine(KillIfHasNotMovedInSeconds(killIfHasNotMovedInSeconds));
    }

    IEnumerator KillIfHasNotMovedInSeconds(float seconds)
    {
        _isRunningKillIfHasNotMovedInSeconds = true;
        var firstPos = motor.GetState().Position;
        yield return new WaitForSeconds(seconds);
        if (Vector3.Distance(firstPos, motor.GetState().Position) < .5f)
        {
            linkValue = 0;
            Kill();
        }

        _isRunningKillIfHasNotMovedInSeconds = false;
    }
    
    void AssignInputs()
    {
        moveInputVector = Inputs.MoveVector;
        lookInputVector = Inputs.LookVector;
        isAiming = Inputs.Aim;
        isFiring = Inputs.Fire;
        jumpRequested = Inputs.Jump;
    }
    
    public virtual void SetDestination(Vector3 destination)
    {
        var valid = NavMesh.CalculatePath(transform.position, destination, NavMesh.AllAreas, NavMeshPath);
        if (valid)
        {
            pathStatus = PathStatus.Pending;
            CurrCornersIndex = 0;
        }
    }

    public override void Kill()
    {
        base.Kill();
        isTargetedByPriestess = false;
        //No idea where this tag trash is used...
        tag = "Dead";
        
        OnAIDeath?.Invoke(this);
        StartCoroutine(LinkSpawner());
        if (this is not Flyer) enabled = false;
    }

    public override void Damage(float damage)
    {
        //invoke onAIHit
        OnAIHit?.Invoke(this, damage);
        base.Damage(damage);

    }

    //TODO: Add some logic to fail or abandon a path if the agent gets stuck.
    /// <summary>
    /// MoveAgent will, if a path is pending, follow each corner of the path until it reaches the end of the corners. When it reaches
    /// the end of the path, it will set pathPending to false and return.
    /// </summary>
    protected virtual void MoveAgent()
    {
        if (pathStatus != PathStatus.Pending || NavMeshPath.corners.Length <= 0) return;
        //Nowhere to move!

        if (debug)
        {
            for (int i = 0; i < NavMeshPath.corners.Length - 1; i++)
            {
                Debug.DrawLine(NavMeshPath.corners[i], NavMeshPath.corners[i + 1], Color.yellow);
            }
        }
        //Draw the path for the SAi.
        
        //We use a point on the collider because using the transform may result in an unreachable point at low stopDistances
        if (Vector3.Distance(coll.ClosestPoint(NavMeshPath.corners[CurrCornersIndex]), NavMeshPath.corners[CurrCornersIndex]) <= stopDistance)
        {
            if (CurrCornersIndex == NavMeshPath.corners.Length - 1)
            {
                pathStatus = PathStatus.Reached;
                return;
            }
            CurrCornersIndex++;
        }
        
        Inputs.MoveVector = (NavMeshPath.corners[CurrCornersIndex] - transform.position).normalized;
        if (lookAtBehavior == LookAtBehavior.AtPath)
            Inputs.LookVector = new Vector3(Inputs.MoveVector.x, 0, Inputs.MoveVector.z);
        //Ignores Y so the character doesn't look at the ground in a silly/goofy way.
    }

    void ExecuteAim()
    {
        if (aimStatus == AimStatus.isHeld) Inputs.Aim = true;
        if (aimStatus == AimStatus.isUp) Inputs.Aim = false;
    }

   
    void ExecuteFire()
    {
        //Clamp accuracy to accepted ranges.
        if (accuracy is < 0 or > 1) Mathf.Clamp(accuracy, 0f, 1f);

        //If the SAi has both a targetPoint and a targetCharacter set, use the targetCharacter.
        var target = targetChar ? targetChar.Collider.bounds.center : targetPoint;
        var targetVelocity = targetChar ? targetChar.motor.Velocity : Vector3.zero;

        NoiseTarget(target, accuracy, targetVelocity);
        //TODO: If using projectile weapon, should lead the target as well.

        targetPoint = target;
        Inputs.Fire = true;
    }

    //TODO: These numbers probably need significant tweaking
    void NoiseTarget(Vector3 targetToNoise, float thisAccuracy, Vector3 targetVelocity)
    {
        //Reduce accuracy if target is moving
        thisAccuracy -= StarfallUtility.Map(targetVelocity.magnitude, 0f, 40f, 0f, 1f);
        var gaussianSpread = StarfallUtility.Map(Mathf.Abs(thisAccuracy - 1f), 0f, 1f, 0f, .3f);
        targetToNoise.x += StarfallUtility.RandGaussian(gaussianSpread);
        targetToNoise.y += StarfallUtility.RandGaussian(gaussianSpread);
        targetToNoise.z += StarfallUtility.RandGaussian(gaussianSpread);
    }
    
    //void LeadTarget()

    protected virtual void LookAtTargetCharacter()
    {
        if (!targetChar) return;
        Inputs.LookVector = (targetChar.Collider.bounds.center - Collider.bounds.center).normalized;
        Inputs.LookVector.y = 0f;
    }

    public virtual void SetTargetCharacter(SCharacter character)
    {
        targetChar = character;
    }

    public SCharacter GetTargetCharacter()
    {
        return targetChar;
    }

    public void SetTargetPoint(Vector3 point)
    {
        targetPoint = point;
    }

    // public Vector3 GetTargetPoint()
    // {
    //     return targetPoint;
    // }

    IEnumerator LinkSpawner()
    {
        
        
        for (var i = 0; i < numLinkDrops; i++)
        {
            var randomX = Random.Range(-1f, 1f);
            var randomY = Random.Range(.3f, .8f);
            var randomZ = Random.Range(-1f, 1f);
            var throwVector = new Vector3(randomX, randomY, randomZ).normalized;
            // var linkDrop = Instantiate(linkObj, transform.position, Quaternion.identity);
            var linkDrop = GameManager.Instance.LinkPool.Get();
            linkDrop.transform.position = transform.position;
            linkDrop.GetComponent<Rigidbody>().AddForce(throwVector, ForceMode.Impulse);
            yield return new WaitForSeconds(.25f);
        }
        yield return new WaitForSeconds(30f);
        Destroy(gameObject);
    }

    public bool HasTargetCharacter()
    {
        if (targetChar == null) return false;
        return targetChar is not null && targetChar.gameObject.activeSelf;
    }

    public void BuffHealth(float buffAmount)
    {
        health *= buffAmount;
        maxHealth = health;
        Debug.Log($"{gameObject.name} health has been buffed by {buffAmount} buffamount, maxHealth is {maxHealth} and health is {health}");
    }

}
