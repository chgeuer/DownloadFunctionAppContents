#!/bin/bash

# inspired by https://gist.github.com/chgeuer/d290c6d3225ffc782a4cbc928064090d

location="westeurope"

isvTenantDomain="geuer-pollmann.de"
isvTenantId="5f9e748d-300b-48f1-85f5-3aa96d6260cb"
isvSubscriptionName="chgeuer-msdn"
isvSubscriptionId="706df49f-998b-40ec-aed3-7f0ce9c67759"
isvResourceGroupName="scanning-demo-isv-side"

customerTenantDomain="chgeuerfte.onmicrosoft.com"
customerTenantId="942023a6-efbe-4d97-a72d-532ef7337595"
customerSubscriptionName="chgeuer-work"
customerSubscriptionId="724467b5-bee4-484b-bf13-d6a5505d2b51"
customerResourceGroupName="customer-rg"

applicationDisplayName="geuer-pollmann.de - A multi-tenant app for scanning functions in a different tenant"
servicePrincipalPasswordFile="/mnt/c/Users/chgeuer/.secrets/principal-for-unencrypted-function-scanning.txt"

az login --tenant "${isvTenantId}"
az account set --subscription "${isvSubscriptionId}"
echo "Logged in to tenant $( az account show | jq '.name' )"

#
# create the application
#
multiTenantAppJSON="$( az ad app create \
  --display-name "${applicationDisplayName}" \
  --sign-in-audience "AzureADMultipleOrgs" )"
echo "${multiTenantAppJSON}" | jq .
#
# Fetch the details
#
multiTenantAppJSON="$( az ad app list \
  --display-name "${applicationDisplayName}" \
  | jq .[0] )"

# echo "${multiTenantAppJSON}" | jq .
multiTenantApp_id="$( echo "${multiTenantAppJSON}" | jq -r '.id' )"
multiTenantApp_appId="$( echo "${multiTenantAppJSON}" | jq -r '.appId' )"

spCreationISVJSON="$( az ad sp create --id "${multiTenantApp_appId}" )"
spCreationISVJSON="$( az ad sp show   --id "${multiTenantApp_appId}" )"
spISVId="$( echo "${spCreationISVJSON}" | jq -r .id )"

echo "ISV-side: 
ISV Multi-tenant app id:    ${multiTenantApp_id} 
ISV Multi-tenant app appId: ${multiTenantApp_appId}
ISV Service principal id:   ${spISVId}"

isvGraphToken="$( az account get-access-token \
   --tenant "${isvTenantId}" \
   --resource-type ms-graph | jq -r .accessToken )"

IFS='' read -r -d '' passwordCreationBody <<EOF
{
  "displayName": "Demo Credential",
  "startDateTime": "$( TZ=GMT date '+%Y-%m-%d' )",
  "endDateTime": "$( TZ=GMT date -d '+1 year' '+%Y-%m-%d' )"
}
EOF

passwordCreationResponseJSON="$( curl \
  --silent \
  --request POST \
  --url "https://graph.microsoft.com/v1.0/applications/${multiTenantApp_id}/addPassword" \
  --header "Content-Type: application/json" \
  --header "Authorization: Bearer ${isvGraphToken}" \
  --data "${passwordCreationBody}" )"

secret="$( echo "${passwordCreationResponseJSON}" | jq -r '.secretText' )"

echo -n "${secret}" > "${servicePrincipalPasswordFile}"

echo "Service principal credential has been stored in ${servicePrincipalPasswordFile}"

accessTokenISVSide="$( curl \
    --silent \
    --request POST \
    --url "https://login.microsoftonline.com/${isvTenantId}/oauth2/v2.0/token" \
    --data-urlencode "response_type=token" \
    --data-urlencode "grant_type=client_credentials" \
    --data-urlencode "client_id=${multiTenantApp_appId}" \
    --data-urlencode "client_secret=$( cat "${servicePrincipalPasswordFile}" )" \
    --data-urlencode "scope=https://management.azure.com/.default" \
    | jq -r '.access_token' )"

echo "accessTokenISVSide: $( echo "${accessTokenISVSide}" \
   | jq -R 'split(".")|.[1]|@base64d|fromjson' \
   | jq '{iss:.iss,aud:.aud,sub:.sub,appid:.appid}' )"

# Customer side
az login --tenant "${customerTenantId}"
echo "Logged in to $( az account show | jq '.name' )"   # Logged in to "chgeuer-work"
spCreationJSON="$( az ad sp create --id "${multiTenantApp_appId}" )" # Create the SP
spCreationJSON="$( az ad sp show   --id "${multiTenantApp_appId}" )" # Display the SP
spCreation_id="$( echo "${spCreationJSON}" | jq -r '.id' )"
multiTenantApp_appId="$( echo "${spCreationJSON}" | jq -r '.appId' )"

echo "Customer Service principal id:    ${spCreation_id} 
Customer Service principal appId (same as multi-tenant app id): ${multiTenantApp_appId}"

#
# Create a custome role on customer side
#
IFS='' read -r -d '' customScannerRoleDefinition <<EOF
{
  "Name": "Checkpoint Scanning Role",
  "Description": "Allows download of Web App credentials and enabling and disabling basic auth for the SCM site.",
  "AssignableScopes": [ "/subscriptions/${customerSubscriptionId}" ],
  "Actions": [ 
     "Microsoft.Web/sites/config/list/Action",
     "Microsoft.Web/sites/basicPublishingCredentialsPolicies/scm/Read",
     "Microsoft.Web/sites/basicPublishingCredentialsPolicies/scm/Write",
     "Microsoft.Web/sites/slots/basicPublishingCredentialsPolicies/scm/Write",
     "Microsoft.Web/sites/slots/basicPublishingCredentialsPolicies/scm/Read"
  ]
}
EOF

az role definition create --role-definition "${customScannerRoleDefinition}"

# List the custom roles
# az role definition list --custom-role-only true --output json --query '[].{roleName:roleName, roleType:roleType}'
roleDescription="$( az role definition list --custom-role-only true --name "Checkpoint Scanning Role" --output json | jq '.[0]' )"
checkpointScannerRoleId="$( echo "${roleDescription}" | jq -r '.name' )"
checkpointScannerRoleName="$( echo "${roleDescription}" | jq -r '.roleName' )"

az role assignment create \
  --role "${checkpointScannerRoleId}" \
  --description "Grant the virus scanning company '${checkpointScannerRoleName}' permission on the subscription" \
  --assignee-object-id "${spCreation_id}" --assignee-principal-type "ServicePrincipal" \
  --scope "/subscriptions/${customerSubscriptionId}"

