node {
	stage 'Checkout'
		checkout scm

	stage 'Build'
		bat 'msbuild.exe restore ProjectSystem.sln'
		bat "\"${tool 'MSBuild'}\" ProjectSystem.sln /p:Configuration=Release /p:Platform=\"Any CPU\" /p:ProductVersion=1.0.0.${env.BUILD_NUMBER}"

	stage 'Archive'
		archive 'ProjectName/bin/Release/**'

}
############ this is test file###################

Node {
     Test
}
