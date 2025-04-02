# Migration Guide: From Manual Scripts to FluxCD GitOps

This guide explains the transition from manual script-based deployment to GitOps using FluxCD.

## Why GitOps with FluxCD?

The previous deployment approach used shell scripts (`build-and-deploy.sh`, `install-metrics-server.sh`, etc.) to manually build images and apply Kubernetes manifests. This approach had several limitations:

- **Manual process**: Required running scripts by hand for every deployment
- **Error-prone**: Scripts could fail mid-deployment, leaving the system in an inconsistent state
- **Not declarative**: The desired state wasn't explicitly defined in version control
- **Lack of automation**: No automatic detection of changes or reconciliation

FluxCD addresses these issues by:

- Automating deployments based on changes in Git repositories
- Continuously reconciling the actual cluster state with the desired state
- Providing visibility into successful and failed deployments
- Supporting progressive delivery patterns

```
/k8s/
  - game-server.yaml
  - master-server.yaml
  - client-simulator.yaml
  - metrics-server/
  - monitoring/
/build-and-deploy.sh
```

## Migration Steps

### 1. Directory Structure Changes

The manual approach used the following structure:

The new FluxCD GitOps approach uses:

```
/flux/
  /base/
    - gotk-components.yaml
  /clusters/
    /default/
      - kustomization.yaml
      - sources.yaml
      - infrastructure.yaml
      - apps.yaml
  /infrastructure/
    /base/
      - metrics-server/
      - monitoring/
    /overlays/
      /default/
  /apps/
    /base/
      - master-server/
      - game-server/
      - client-simulator/
    /overlays/
      /dev/
```

### 2. Key Changes

#### Manifest Modifications

- Added FluxCD-specific annotations to the deployment YAML files
- Organized manifests into a Kustomize-based structure
- Created kustomization patches for environment-specific values
- Converted shell script logic to declarative Kubernetes resources

#### Helm Charts for Infrastructure

- Replaced manual Prometheus and Grafana installations with Helm charts
- Configured the metrics-server as a Helm release
- Set up automated dashboard imports as ConfigMaps

#### Automation

- Git repositories are now monitored automatically by FluxCD
- Changes are applied automatically when pushed to the repository
- No need to manually run deployment scripts
- Image updates can be automated with FluxCD image policies

### 3. Migration Checklist

To complete the migration:

- [X] Create FluxCD component manifests
- [X] Reorganize Kubernetes manifests into Kustomize structure
- [X] Convert monitoring setup to Helm releases
- [X] Add overlay configurations for different environments
- [X] Create GitRepository sources for FluxCD
- [X] Update documentation to reflect GitOps approach
- [ ] Bootstrap FluxCD on the target cluster
- [ ] Configure container registry access for FluxCD
- [ ] Set up automated builds (CI pipeline)

### 4. From Manual to GitOps Workflow

#### Previous Workflow

1. Make code changes
2. Run `./build-and-deploy.sh` to build and push images
3. The script would apply changes to Kubernetes
4. Manually verify deployment

#### New GitOps Workflow

1. Make code changes
2. Build and push images (via CI/CD or manually)
3. Update the image tag in the Git repository
4. Commit and push changes to Git
5. FluxCD detects changes and applies them to the cluster
6. View deployment status in the FluxCD dashboard or via CLI

## Next Steps

1. **Install the Flux CLI**: Follow the installation instructions at https://fluxcd.io/docs/installation/
2. **Bootstrap FluxCD**: Run the bootstrap command with your Git repository details
3. **Monitor the reconciliation**: Use `flux get kustomizations` to check deployment status

For detailed instructions, refer to the [FluxCD documentation](https://fluxcd.io/docs/get-started/).

## Common Questions

**Q: How do I revert a bad deployment?**
A: Simply revert the Git commit that caused the issue and push the change. FluxCD will automatically roll back to the previous state.

**Q: How do I deploy to a different environment?**
A: Create a new overlay in `flux/apps/overlays/` for your environment and configure a new Kustomization to point to it.

**Q: Can I still manually deploy in emergencies?**
A: Yes, you can use `kubectl apply` directly, but FluxCD will eventually reconcile back to the state defined in Git. For emergencies, it's better to make a quick fix in Git.

**Q: How do I see what FluxCD is doing?**
A: Use `flux get all` to view all resources and their reconciliation status.
