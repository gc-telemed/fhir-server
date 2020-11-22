﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Exceptions;
using Microsoft.Health.Fhir.Core.Features.Operations.ConvertData.Models;
using Microsoft.Health.Fhir.Core.Messages.ConvertData;
using Microsoft.Health.Fhir.Liquid.Converter;
using Microsoft.Health.Fhir.Liquid.Converter.Exceptions;
using Microsoft.Health.Fhir.Liquid.Converter.Hl7v2;
using Microsoft.Health.Fhir.Liquid.Converter.Models;

namespace Microsoft.Health.Fhir.Core.Features.Operations.ConvertData
{
    public class ConvertDataEngine : IConvertDataEngine
    {
        private readonly IConvertDataTemplateProvider _convertDataTemplateProvider;
        private readonly ConvertDataConfiguration _convertDataConfiguration;
        private readonly ILogger<ConvertDataEngine> _logger;

        private readonly Dictionary<ConversionInputDataType, IFhirConverter> _converterMap = new Dictionary<ConversionInputDataType, IFhirConverter>();

        public ConvertDataEngine(
            IConvertDataTemplateProvider convertDataTemplateProvider,
            IOptions<ConvertDataConfiguration> convertDataConfiguration,
            ILogger<ConvertDataEngine> logger)
        {
            EnsureArg.IsNotNull(convertDataTemplateProvider, nameof(convertDataTemplateProvider));
            EnsureArg.IsNotNull(convertDataConfiguration, nameof(convertDataConfiguration));
            EnsureArg.IsNotNull(logger, nameof(logger));

            _convertDataTemplateProvider = convertDataTemplateProvider;
            _convertDataConfiguration = convertDataConfiguration.Value;
            _logger = logger;

            InitializeConvertProcessors();
        }

        public async Task<ConvertDataResponse> Process(ConvertDataRequest convertRequest, CancellationToken cancellationToken)
        {
            var templateCollection = await _convertDataTemplateProvider.GetTemplateCollectionAsync(convertRequest, cancellationToken);
            var result = GetConvertDataResult(convertRequest, new Hl7v2TemplateProvider(templateCollection), cancellationToken);

            return new ConvertDataResponse(result);
        }

        private string GetConvertDataResult(ConvertDataRequest convertRequest, ITemplateProvider templateProvider, CancellationToken cancellationToken)
        {
            var converter = _converterMap.GetValueOrDefault(convertRequest.InputDataType);
            if (converter == null)
            {
                // This case should never happen.
                _logger.LogError("Invalid input data type for conversion.");
                throw new RequestNotValidException("Invalid input data type for conversion.");
            }

            try
            {
                return converter.Convert(convertRequest.InputData, convertRequest.EntryPointTemplate, templateProvider, cancellationToken);
            }
            catch (DataParseException dpe)
            {
                _logger.LogError(dpe, "Unable to parse the input data.");
                throw new InputDataParseErrorException(string.Format(Resources.InputDataParseError, convertRequest.InputDataType.ToString()), dpe);
            }
            catch (ConverterInitializeException ie)
            {
                _logger.LogError(ie, "Fail to initialize the convert engine.");
                throw new ConvertEngineInitializeException(Resources.ConvertDataEngineInitializeFailed, ie);
            }
            catch (FhirConverterException fce)
            {
                if (fce.InnerException is TimeoutException)
                {
                    _logger.LogError(fce, "Data convert operation timed out.");
                    throw new ConvertDataTimeoutException(Resources.ConvertDataOperationTimeout, fce.InnerException);
                }

                _logger.LogError(fce, "Data convert process failed.");
                throw new ConvertDataFailedException(string.Format(Resources.ConvertDataFailed, fce.Message), fce);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception: data convert process failed.");
                throw new ConvertDataFailedException(string.Format(Resources.ConvertDataFailed, ex.Message), ex);
            }
        }

        /// <summary>
        /// In order to terminate long running templates, we add timeout setting to DotLiquid rendering context,
        /// which throws a Timeout Exception when render process elapsed longer than timeout threshold.
        /// Reference: https://github.com/dotliquid/dotliquid/blob/master/src/DotLiquid/Context.cs
        /// </summary>
        private void InitializeConvertProcessors()
        {
            var processorSetting = new ProcessorSettings
            {
                TimeOut = (int)_convertDataConfiguration.OperationTimeout.TotalMilliseconds,
            };

            _converterMap.Add(ConversionInputDataType.Hl7v2, new Hl7v2Processor(processorSetting));
        }
    }
}