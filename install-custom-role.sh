#!/bin/bash

subscriptionId="$( az account show | jq -r '.id' )"

IFS='' read -r -d '' customScannerRoleDefinitionJSON <<EOF
{
  "Name": "Checkpoint Scanning Role",
  "Description": "Allows download of Web App credentials and enabling and disabling basic auth for the SCM site.",
  "AssignableScopes": [ "/subscriptions/${subscriptionId}" ],
  "Actions": [ 
     "Microsoft.Web/sites/config/list/Action",
     "Microsoft.Web/sites/basicPublishingCredentialsPolicies/scm/Read",
     "Microsoft.Web/sites/basicPublishingCredentialsPolicies/scm/Write",
     "Microsoft.Web/sites/slots/basicPublishingCredentialsPolicies/scm/Write",
     "Microsoft.Web/sites/slots/basicPublishingCredentialsPolicies/scm/Read"
  ]
}
EOF

az role definition create --role-definition "${customScannerRoleDefinitionJSON}"
