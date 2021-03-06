﻿/*
 * Copyright(c) 2017 Microsoft Corporation. All rights reserved. 
 * 
 * This code is licensed under the MIT License (MIT). 
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal 
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
 * of the Software, and to permit persons to whom the Software is furnished to do 
 * so, subject to the following conditions: 
 * 
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software. 
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, 
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE. 
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

namespace BingMapsRESTToolkit
{
    /// <summary>
    /// A request that calculates a distance matrix between origins and destinations.
    /// </summary>
    [DataContract]
    public class DistanceMatrixRequest : BaseRestRequest
    {
        #region Private Properties

        /// <summary>
        /// The maximum number of coordinate pairs that can be in a standar distance matrix request.
        /// </summary>
        private const int MaxCoordinatePairs = 625;

        /// <summary>
        /// The maximum number of coordinate pairs that can be in an Async request for a distance matrix histogram.
        /// </summary>
        private const int MaxAsyncCoordinatePairsHistogram = 100;

        /// <summary>
        /// The maximum number of hours between the start and end time when calculating a distance matrix histogram. 
        /// </summary>
        private const double MaxTimeSpanHours = 24;

        /// <summary>
        /// The maximium number of times the retry the status check if it fails. This will allow for possible connection issues.
        /// </summary>
        private const int MaxStatusCheckRetries = 3;

        /// <summary>
        /// Number of seconds to delay a retry of a status check.
        /// </summary>
        private const int StatusCheckRetryDelay = 10;

        #endregion 

        #region Constructure

        /// <summary>
        /// A request that calculates a distance matrix between origins and destinations.
        /// </summary>
        public DistanceMatrixRequest() : base()
        {
            TravelMode = TravelModeType.Driving;
            DistanceUnits = DistanceUnitType.Kilometers;
            TimeUnits = TimeUnitType.Seconds;
            Resolution = 1;
        }

        #endregion

        #region Public Properties

        /// <summary>
        /// Required. List of origins described as coordinate pairs with latitudes and longitudes. 
        /// </summary>
        public List<SimpleWaypoint> Origins { get; set; }

        /// <summary>
        /// List of destinations described as coordinate pairs with latitudes and longitudes.
        /// </summary>
        public List<SimpleWaypoint> Destinations { get; set; }

        /// <summary>
        /// Specifies the mode of transportation to use when calculating the distance matrix. Can be one of the following values: driving [default], walking, transit.
        /// </summary>
        public TravelModeType TravelMode { get; set; }

        /// <summary>
        /// Optional for Driving. Specifies the start or departure time of the matrix to calculate and uses traffic data in calculations. 
        /// This option is only supported when the mode is set to driving.
        /// </summary>
        public DateTime? StartTime { get; set; }

        /// <summary>
        /// Optional for Driving. If specified, a matrix based on traffic data with contain a histogram of travel times and distances for 
        /// the specified resolution (default is 15 minutes) between the start and end times. A start time must be specified for the request to be valid. 
        /// This option is only supported when the mode is set to driving.
        /// </summary>
        public DateTime? EndTime { get; set; }

        /// <summary>
        /// The number of intervals to calculate a histogram of data for each cell for where a single interval is a quarter 
        /// of an hour, 2 intervals would be 30 minutes, 3 intervals would be 45 minutes, 4 intervals would be for an hour. 
        /// If start time is specified and resolution is not, it will default to an interval of 1 (15 minutes).
        /// </summary>
        public int Resolution { get; set; }

        /// <summary>
        /// The units to use for distance. Default: Kilometers.
        /// </summary>
        public DistanceUnitType DistanceUnits { get; set; }

        /// <summary>
        /// The units to use for time. Default: Seconds.
        /// </summary>
        public TimeUnitType TimeUnits { get; set; }

        #endregion

        #region Public Methods

        /// <summary>
        /// Executes the request.
        /// </summary>
        /// <returns>A response containing the requested distance matrix.</returns>
        public override async Task<Response> Execute() {
            return await this.Execute(null);
        }

        /// <summary>
        /// Executes the request.
        /// </summary>
        /// <param name="remainingTimeCallback">A callback function in which the estimated remaining time is sent.</param>
        /// <returns>A response containing the requested distance matrix.</returns>
        public override async Task<Response> Execute(Action<int> remainingTimeCallback)
        {
            //Make sure all origins and destinations are geocoded.
            await GeocodeWaypoints();
            
            var requestUrl = GetRequestUrl();
            var requestBody = GetPostRequestBody();

            Response response = null;

            using (var responseStream = await ServiceHelper.PostStringAsync(new Uri(requestUrl), requestBody, "application/json"))
            {
                response = ServiceHelper.DeserializeStream<Response>(responseStream);
            }

            if(response != null && response.ErrorDetails != null && response.ErrorDetails.Length > 0)
            {
                throw new Exception("Error: " + response.ErrorDetails[0]);
            }

            if (response != null && response.ResourceSets != null && response.ResourceSets.Length > 0 && response.ResourceSets[0].Resources != null && response.ResourceSets[0].Resources.Length > 0)
            {
                if (response.ResourceSets[0].Resources[0] is DistanceMatrixAsyncStatus && !string.IsNullOrEmpty((response.ResourceSets[0].Resources[0] as DistanceMatrixAsyncStatus).RequestId))
                {
                    var status = response.ResourceSets[0].Resources[0] as DistanceMatrixAsyncStatus;
                    var statusUrl = new Uri(status.CallbackUrl);
                    //var statusUrl = new Uri(this.Domain + "Routes/DistanceMatrixAsyncCallback?requestId=" + status.RequestId + "&key=" + this.BingMapsKey);

                    if (status.CallbackInSeconds > 0 || !status.IsCompleted || string.IsNullOrEmpty(status.ResultUrl))
                    {
                        remainingTimeCallback?.Invoke(status.CallbackInSeconds);

                        //Wait remaining seconds.
                        await Task.Delay(TimeSpan.FromSeconds(status.CallbackInSeconds));
                        
                        status = await MonitorAsyncStatus(statusUrl, 0, remainingTimeCallback);
                    }

                    if (status != null)
                    {
                        if (status.IsCompleted && !string.IsNullOrEmpty(status.ResultUrl))
                        {
                            try
                            {
                                using (var resultStream = await ServiceHelper.GetStreamAsync(new Uri(status.ResultUrl)))
                                {
                                    DistanceMatrix dm = ServiceHelper.DeserializeStream<DistanceMatrix>(resultStream);

                                    response.ResourceSets[0].Resources[0] = dm;

                                    //TODO: Overwrite origins/destinations for now as we have added support for geocoding in this library, but this is not yet supported by the Distance Matirx API.
                                    dm.Origins = this.Origins.ToArray();
                                    dm.Destinations = this.Destinations.ToArray();
                                }
                            }
                            catch(Exception ex)
                            {
                                response.ResourceSets[0].Resources[0] = new DistanceMatrix()
                                {
                                    ErrorMessage = "There was an issue downloading and serializing the results. Results Download URL: " + status.ResultUrl
                                };
                            }
                        }
                        else if (!status.IsAccepted)
                        {
                            response.ResourceSets[0].Resources[0] = new DistanceMatrix()
                            {
                                ErrorMessage = "The request was not accepted."
                            };
                        }
                        else if (!string.IsNullOrEmpty(status.ErrorMessage))
                        {
                            response.ResourceSets[0].Resources[0] = new DistanceMatrix()
                            {
                                ErrorMessage = status.ErrorMessage
                            };
                        }
                    }

                    return response;
                }
                else if (response.ResourceSets[0].Resources[0] is DistanceMatrix && (response.ResourceSets[0].Resources[0] as DistanceMatrix).Results != null)
                {
                    DistanceMatrix dm = response.ResourceSets[0].Resources[0] as DistanceMatrix;

                    //TODO: Overwrite origins/destinations for now as we have added support for geocoding in this library, but this is not yet supported by the Distance Matirx API.
                    dm.Origins = this.Origins.ToArray();
                    dm.Destinations = this.Destinations.ToArray();

                    if (dm.Results != null) {
                        return response;
                    }
                    else if(!string.IsNullOrEmpty(dm.ErrorMessage))
                    {
                        var msg = "Error: " + (response.ResourceSets[0].Resources[0] as DistanceMatrix).ErrorMessage;
                        throw new Exception(msg);
                    }
                }
                else if (response.ResourceSets[0].Resources[0] is DistanceMatrixAsyncStatus && !string.IsNullOrEmpty((response.ResourceSets[0].Resources[0] as DistanceMatrixAsyncStatus).ErrorMessage))
                {
                    var msg = "Error: " + (response.ResourceSets[0].Resources[0] as DistanceMatrixAsyncStatus).ErrorMessage;
                    throw new Exception(msg);
                }
                else if (response.ResourceSets[0].Resources[0] is DistanceMatrix && !string.IsNullOrEmpty((response.ResourceSets[0].Resources[0] as DistanceMatrix).ErrorMessage))
                {
                    var msg = "Error: " + (response.ResourceSets[0].Resources[0] as DistanceMatrix).ErrorMessage;
                    throw new Exception(msg);
                }
            }

            return null;           
        }

        /// <summary>
        /// Geocodes the origins and destinations.
        /// </summary>
        /// <returns>A task for geocoding the origins and destinations.</returns>
        public async Task GeocodeWaypoints()
        {
            //Ensure all the origins are geocoded.
            if (Origins != null)
            {
                await SimpleWaypoint.GeocodeWaypoints(Origins, this);
            }

            //Ensure all the destinations are geocoded.
            if (Destinations != null)
            {
                await SimpleWaypoint.GeocodeWaypoints(Destinations, this);
            }
        }

        /// <summary>
        /// Calculates a Distance Matrix for the origins and destinations based on the euclidean distance (straight line/as the crow flies). This calculation only uses; Origins, Destinations, and Distance Units properties from the request and only calculates travel distance.
        /// </summary>
        /// <returns>A Distance Matrix for the origins and destinations based on the euclidean distance (straight line/as the crow flies).</returns>
        public async Task<DistanceMatrix> GetEuclideanDistanceMatrix()
        {
            if(this.Origins != null && this.Origins.Count > 0)
            //Make sure all origins and destinations are geocoded.
            await GeocodeWaypoints();

            var dm = new DistanceMatrix()
            {
                Origins = this.Origins.ToArray()
            };

            int cnt = 0;

            if (this.Destinations == null || this.Destinations.Count == 0)
            {
                dm.Destinations = this.Origins.ToArray();
                dm.Results = new DistanceMatrixCell[this.Origins.Count * this.Origins.Count];

                for (var i = 0; i < Origins.Count; i++)
                {
                    for (var j = 0; j < Origins.Count; j++)
                    {
                        dm.Results[cnt] = new DistanceMatrixCell()
                        {
                            OriginIndex = i,
                            DestinationIndex = j,
                            TravelDistance = SpatialTools.HaversineDistance(Origins[i].Coordinate, Origins[j].Coordinate, DistanceUnits)
                        };

                        cnt++;
                    }
                }
            }
            else
            {
                dm.Destinations = this.Destinations.ToArray();
                dm.Results = new DistanceMatrixCell[this.Origins.Count * this.Destinations.Count];

                for (var i = 0; i < Origins.Count; i++)
                {
                    for (var j = 0; j < Destinations.Count; j++)
                    {
                        dm.Results[cnt] = new DistanceMatrixCell()
                        {
                            OriginIndex = i,
                            DestinationIndex = j,
                            TravelDistance = SpatialTools.HaversineDistance(Origins[i].Coordinate, Destinations[j].Coordinate, DistanceUnits)
                        };

                        cnt++;
                    }
                }
            }

            return dm;
        }

        /// <summary>
        /// Returns the number of coordinate pairs that would be in the resulting matrix based on the number of origins and destinations in the request.
        /// </summary>
        /// <returns>The number of coordinate pairs that would be in the resulting matrix based on the number of origins and destinations in the request.</returns>
        public int GetNumberOfCoordinatePairs()
        {
            int numCoordPairs = Origins.Count;

            if (Destinations != null)
            {
                numCoordPairs *= Destinations.Count;
            }
            else
            {
                numCoordPairs *= Origins.Count;
            }

            return numCoordPairs;
        }
        
        /// <summary>
        /// Gets the request URL to perform a query for a distance matrix when using POST.
        /// </summary>
        /// <returns>A request URL to perform a query for a distance matrix when using POST.</returns>
        public override string GetRequestUrl()
        {
            //Matrix when using POST
            //https://dev.virtualearth.net/REST/v1/Routes/DistanceMatrix?key=BingMapsKey

            ValidateLocations(Origins, "Origin");
            
            if (Destinations != null)
            {
                ValidateLocations(Destinations, "Destination");
            }

            bool isAsyncRequest = false;

            int numCoordPairs = GetNumberOfCoordinatePairs();

            if (numCoordPairs > MaxCoordinatePairs)
            {
                throw new Exception("The number of Origins and Destinations provided would result in a matrix that has more than 625 coordinate pairs.");
            }

            if (StartTime.HasValue)
            {
                if(TravelMode != TravelModeType.Driving)
                {
                    throw new Exception("Start time parameter can only be used with the driving travel mode.");
                }

                //Since start time is specified, an asynchronous request will be made which allows up to 100 coordinate pairs in the matrix (coordinate pairs).
                if (numCoordPairs > MaxAsyncCoordinatePairsHistogram)
                {
                    throw new Exception("The number of Origins and Destinations provided would result in a matrix that has more than 100 coordinate pairs which is the limit when a histogram is requested.");
                }

                isAsyncRequest = true;
            }

            if (EndTime.HasValue)
            {
                if(!StartTime.HasValue)
                {
                    throw new Exception("End time specified without a corresponding stat time.");
                }

                var timeSpan = EndTime.Value.Subtract(StartTime.Value);

                if(timeSpan.TotalHours > MaxTimeSpanHours)
                {
                    throw new Exception("The time span between start and end time is more than 24 hours.");
                }

                if(Resolution < 0 || Resolution > 4)
                {
                    throw new Exception("Invalid resolution specified. Should be 1, 2, 3, or 4.");
                }
            }
            
            return this.Domain + "Routes/DistanceMatrix" + ((isAsyncRequest)? "Async?" : "" + "?") + GetBaseRequestUrl();
        }

        /// <summary>
        /// Returns a JSON string object representing the request. 
        /// </summary>
        /// <returns></returns>
        public string GetPostRequestBody()
        {
            //Build the JSON object using string builder as faster than JSON serializer.

            var sb = new StringBuilder();

            sb.Append("{\"origins\":[");

            foreach (var o in Origins)
            {
                sb.AppendFormat("{{\"latitude\":{0:0.#####},\"longitude\":{1:0.#####}}},", o.Latitude, o.Longitude);
            }

            //Remove trailing comma.
            sb.Length--;

            sb.Append("]");

            if (Destinations != null && Destinations.Count > 0)
            {
                sb.Append(",\"destinations\":[");

                foreach (var d in Destinations)
                {
                    sb.AppendFormat("{{\"latitude\":{0:0.#####},\"longitude\":{1:0.#####}}},", d.Latitude, d.Longitude);
                }

                //Remove trailing comma.
                sb.Length--;

                sb.Append("]");
            }

            string mode;

            switch (TravelMode)
            {
                case TravelModeType.Transit:
                    mode = "transit";
                    break;
                case TravelModeType.Walking:
                    mode = "walking";
                    break;
                case TravelModeType.Driving:
                default:
                    mode = "driving";
                    break;
            }

            sb.AppendFormat(",\"travelMode\":\"{0}\"", mode);

            if (StartTime.HasValue)
            {
                sb.AppendFormat(DateTimeFormatInfo.InvariantInfo, ",\"startTime\":\"{0:O}\"", StartTime.Value);

                if (EndTime.HasValue)
                {
                    sb.AppendFormat(DateTimeFormatInfo.InvariantInfo, ",\"endTime\":\"{0:O}\"", EndTime.Value);
                    sb.AppendFormat(",\"resolution\":{0}", Resolution);
                }
            }

            string tu;

            switch (TimeUnits)
            {
                case TimeUnitType.Minutes:
                    tu = "minutes";
                    break;
                case TimeUnitType.Seconds:
                default:
                    tu = "seconds";
                    break;
            }

            sb.AppendFormat(",\"timeUnit\":\"{0}\"", tu);

            string du;

            switch (DistanceUnits)
            {
                case DistanceUnitType.Miles:
                    du = "mile";
                    break;
                case DistanceUnitType.Kilometers:
                default:
                    du = "kilometer";
                    break;
            }

            sb.AppendFormat(",\"distanceUnit\":\"{0}\"}}", du);            

            return sb.ToString();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Varifies that an array of locations are valid and geocoded.
        /// </summary>
        /// <param name="locations">The array of locations to validate.</param>
        /// <param name="name">The name of the locations array.</param>
        private void ValidateLocations(List<SimpleWaypoint> locations, string name)
        {
            if (locations == null)
            {
                throw new Exception(name + "s not specified.");
            }
            else if (locations.Count < 1)
            {
                throw new Exception("Not enough " + name + "s specified.");
            }

            //Verify all waypoints are geocoded.
            for (int i = 0; i < locations.Count; i++)
            {
                if (locations[i] == null) {
                    throw new Exception(name + " " + i + " is null.");
                }
                else if(locations[i].Coordinate == null)
                {
                    if (!string.IsNullOrEmpty(locations[i].Address))
                    {
                        throw new Exception(name + " " + i + " has no location information.");
                    }
                    else
                    {
                        throw new Exception(name + " " + i + " not geocoded. Address: " + locations[i].Address);
                    }
                }
            }
        }

        /// <summary>
        /// Monitors the status of an async distance matrix request.
        /// </summary>
        /// <param name="statusUrl">The status URL for the async request.</param>
        /// <param name="failedTries">The number of times the status check has failed consecutively.</param>
        /// <param name="remainingTimeCallback">A callback function in whichthe estimated remaining time is sent.</param>
        /// <returns>The final async status when the request completed, had an error, or was not accepted.</returns>
        private async Task<DistanceMatrixAsyncStatus> MonitorAsyncStatus(Uri statusUrl, int failedTries, Action<int> remainingTimeCallback)
        {
            DistanceMatrixAsyncStatus status = null;

            try
            {
                using (var rs = await ServiceHelper.GetStreamAsync(statusUrl))
                {
                    var r = ServiceHelper.DeserializeStream<Response>(rs);

                    if (r != null)
                    {
                        if(r.ErrorDetails != null && r.ErrorDetails.Length > 0)
                        {
                            throw new Exception(r.ErrorDetails[0]);
                        }
                        else if (r.ResourceSets != null && r.ResourceSets.Length > 0 && r.ResourceSets[0].Resources != null && r.ResourceSets[0].Resources.Length > 0 && r.ResourceSets[0].Resources[0] is DistanceMatrixAsyncStatus)
                        {
                            status = r.ResourceSets[0].Resources[0] as DistanceMatrixAsyncStatus;

                            if (status.CallbackInSeconds > 0)
                            {
                                remainingTimeCallback?.Invoke(status.CallbackInSeconds);

                                //Wait remaining seconds.
                                await Task.Delay(TimeSpan.FromSeconds(status.CallbackInSeconds));
                                return await MonitorAsyncStatus(statusUrl, 0, remainingTimeCallback);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                //Check to see how many times the status check has failed consecutively.
                if (failedTries < MaxStatusCheckRetries)
                {
                    //Wait some time and try again.
                    await Task.Delay(TimeSpan.FromSeconds(StatusCheckRetryDelay));
                    return await MonitorAsyncStatus(statusUrl, failedTries + 1, remainingTimeCallback);
                }
                else 
                {
                    status.ErrorMessage = "Failed to get status, and exceeded the maximium of " + MaxStatusCheckRetries + " retries. Error message: " + ex.Message;
                    status.CallbackInSeconds = -1;
                    status.IsCompleted = false;
                }
            }

            //Should only get here is the request has completed, was not accepted or there was an error.
            return status;
        }

        #endregion
    }
}